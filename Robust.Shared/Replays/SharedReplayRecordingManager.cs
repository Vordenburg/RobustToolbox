using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SharpZstd.Interop;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;
using Robust.Shared.Network;
using YamlDotNet.RepresentationModel;
using static Robust.Shared.Replays.ReplayConstants;

namespace Robust.Shared.Replays;

internal abstract partial class SharedReplayRecordingManager : IReplayRecordingManager
{
    // date format for default replay names. Like the sortable template, but without colons.
    public const string DefaultReplayNameFormat = "yyyy-MM-dd_HH-mm-ss";

    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] protected readonly INetConfigurationManager NetConf = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IRobustSerializer _serializer = default!;
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    public event Action<MappingDataNode, List<object>>? RecordingStarted;
    public event Action<MappingDataNode>? RecordingStopped;
    public event Action<IWritableDirProvider, ResPath>? RecordingFinished;

    private ISawmill _sawmill = default!;
    private List<object> _queuedMessages = new();

    // Config variables.
    private int _maxCompressedSize;
    private int _maxUncompressedSize;
    private int _tickBatchSize;
    private bool _enabled;

    public bool IsRecording => _recState != null;
    private RecordingState? _recState;

    /// <inheritdoc/>
    public virtual void Initialize()
    {
        _sawmill = _logManager.GetSawmill("replay");

        NetConf.OnValueChanged(CVars.ReplayMaxCompressedSize, (v) => _maxCompressedSize = v * 1024, true);
        NetConf.OnValueChanged(CVars.ReplayMaxUncompressedSize, (v) => _maxUncompressedSize = v * 1024, true);
        NetConf.OnValueChanged(CVars.ReplayTickBatchSize, (v) => _tickBatchSize = v * 1024, true);
        NetConf.OnValueChanged(CVars.NetPVSCompressLevel, OnCompressionChanged);
    }

    public virtual bool CanStartRecording()
    {
        return !IsRecording && _enabled;
    }

    private void OnCompressionChanged(int value)
    {
        // Update compression level on running replay.
        _recState?.CompressionContext.SetParameter(ZSTD_cParameter.ZSTD_c_compressionLevel, value);
    }

    public void SetReplayEnabled(bool value)
    {
        if (!value)
            StopRecording();

        _enabled = value;
    }

    /// <inheritdoc/>
    public void StopRecording()
    {
        if (!IsRecording)
            return;

        try
        {
            WriteGameState(continueRecording: false);
            _sawmill.Info("Replay recording stopped!");
        }
        catch
        {
            Reset();
            throw;
        }

        UpdateWriteTasks();
    }

    public void Update(GameState? state)
    {
        UpdateWriteTasks();

        if (state == null || _recState == null)
            return;

        try
        {
            _serializer.SerializeDirect(_recState.Buffer, state);
            _serializer.SerializeDirect(_recState.Buffer, new ReplayMessage { Messages = _queuedMessages });
            _queuedMessages.Clear();

            bool continueRecording = _recState.EndTime == null || _recState.EndTime.Value >= Timing.CurTime;
            if (!continueRecording)
                _sawmill.Info("Reached requested replay recording length. Stopping recording.");

            if (!continueRecording || _recState.Buffer.Length > _tickBatchSize)
                WriteGameState(continueRecording);
        }
        catch (Exception e)
        {
            _sawmill.Log(LogLevel.Error, e, "Caught exception while saving replay data.");
            StopRecording();
        }
    }

    /// <inheritdoc/>
    public virtual bool TryStartRecording(
        IWritableDirProvider directory,
        string? name = null,
        bool overwrite = false,
        TimeSpan? duration = null)
    {
        if (!CanStartRecording())
            return false;

        // If the previous recording had exceptions, throw them now before starting a new recording.
        UpdateWriteTasks();

        name ??= DefaultReplayFileName();
        var filePath = new ResPath(name).Clean();

        if (filePath.Extension != "zip")
            filePath = filePath.WithName(filePath.Filename + ".zip");

        var basePath = new ResPath(NetConf.GetCVar(CVars.ReplayDirectory)).ToRootedPath();
        filePath = basePath / filePath.ToRelativePath();

        // Make sure to create parent directory.
        directory.CreateDir(filePath.Directory);

        if (directory.Exists(filePath))
        {
            if (overwrite)
            {
                _sawmill.Info($"Replay file {filePath} already exists. Overwriting.");
                directory.Delete(filePath);
            }
            else
            {
                _sawmill.Info($"Replay file {filePath} already exists. Aborting recording.");
                return false;
            }
        }

        var file = directory.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var zip = new ZipArchive(file, ZipArchiveMode.Create);

        var context = new ZStdCompressionContext();
        context.SetParameter(ZSTD_cParameter.ZSTD_c_compressionLevel, NetConf.GetCVar(CVars.NetPVSCompressLevel));
        var buffer = new MemoryStream(_tickBatchSize * 2);

        TimeSpan? recordingEnd = null;
        if (duration != null)
            recordingEnd = Timing.CurTime + duration.Value;

        var commandQueue = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(NetConf.GetCVar(CVars.ReplayWriteChannelSize))
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }
        );

        var writeTask = Task.Run(() => WriteQueueLoop(commandQueue.Reader, zip));

        _recState = new RecordingState(
            zip,
            buffer,
            context,
            Timing.CurTick,
            Timing.CurTime,
            recordingEnd,
            commandQueue.Writer,
            writeTask,
            directory,
            filePath
        );

        try
        {
            WriteContentBundleInfo(_recState);
            WriteInitialMetadata(name, _recState);
        }
        catch
        {
            Reset();
            throw;
        }

        _sawmill.Info("Started recording replay...");
        UpdateWriteTasks();
        return true;
    }

    protected abstract string DefaultReplayFileName();

    public abstract void RecordServerMessage(object obj);
    public abstract void RecordClientMessage(object obj);

    public void RecordReplayMessage(object obj)
    {
        if (!IsRecording)
            return;

        DebugTools.Assert(obj.GetType().HasCustomAttribute<NetSerializableAttribute>());
        _queuedMessages.Add(obj);
    }

    private void WriteGameState(bool continueRecording = true)
    {
        DebugTools.Assert(_recState != null);

        _recState.Buffer.Position = 0;

        // Compress stream to buffer.
        // First 4 bytes of buffer are reserved for the length of the uncompressed stream.
        var bound = ZStd.CompressBound((int)_recState.Buffer.Length);
        var buf = ArrayPool<byte>.Shared.Rent(4 + bound);
        var length = _recState.CompressionContext.Compress2(buf.AsSpan(4, bound), _recState.Buffer.AsSpan());
        BitConverter.TryWriteBytes(buf, (int)_recState.Buffer.Length);
        WritePooledBytes(
            _recState,
            ReplayZipFolder / $"{DataFilePrefix}{_recState.Index++}.{Ext}",
            buf, 4 + length, CompressionLevel.NoCompression);

        _recState.UncompressedSize += (int)_recState.Buffer.Length;
        _recState.CompressedSize += length;
        if (_recState.UncompressedSize >= _maxUncompressedSize || _recState.CompressedSize >= _maxCompressedSize)
        {
            _sawmill.Info("Reached max replay recording size. Stopping recording.");
            continueRecording = false;
        }

        if (continueRecording)
            _recState.Buffer.SetLength(0);
        else
            WriteFinalMetadata(_recState);
    }

    protected virtual void Reset()
    {
        if (_recState == null)
            return;

        _recState.CompressionContext.Dispose();
        // File stream is always disposed from the worker task.
        _recState.WriteCommandChannel.Complete();

        _recState = null;
    }

    /// <summary>
    ///     Write general replay data required to read the rest of the replay. We write this at the beginning rather than at the end on the off-chance that something goes wrong along the way and the recording is incomplete.
    /// </summary>
    private void WriteInitialMetadata(string name, RecordingState recState)
    {
        var (stringHash, stringData) = _serializer.GetStringSerializerPackage();
        var extraData = new List<object>();

        // Saving YAML data. This gets overwritten later anyways, this is mostly in case something goes wrong.
        {
            var yamlMetadata = new MappingDataNode();
            yamlMetadata[MetaKeyTime] = new ValueDataNode(DateTime.UtcNow.ToString(CultureInfo.InvariantCulture));
            yamlMetadata[MetaKeyName] = new ValueDataNode(name);

            // version info
            yamlMetadata[MetaKeyEngineVersion] = new ValueDataNode(NetConf.GetCVar(CVars.BuildEngineVersion));
            yamlMetadata[MetaKeyForkId] = new ValueDataNode(NetConf.GetCVar(CVars.BuildForkId));
            yamlMetadata[MetaKeyForkVersion] = new ValueDataNode(NetConf.GetCVar(CVars.BuildVersion));

            // Hash data
            yamlMetadata[MetaKeyTypeHash] = new ValueDataNode(Convert.ToHexString(_serializer.GetSerializableTypesHash()));
            yamlMetadata[MetaKeyStringHash] = new ValueDataNode(Convert.ToHexString(stringHash));
            yamlMetadata[MetaKeyComponentHash] = new ValueDataNode(Convert.ToHexString(_factory.GetHash(true)));

            // Time data
            var timeBase = Timing.TimeBase;
            yamlMetadata[MetaKeyStartTick] = new ValueDataNode(recState.StartTick.Value.ToString());
            yamlMetadata[MetaKeyBaseTick] = new ValueDataNode(timeBase.Item2.Value.ToString());
            yamlMetadata[MetaKeyBaseTime] = new ValueDataNode(timeBase.Item1.Ticks.ToString());
            yamlMetadata[MetaKeyStartTime] = new ValueDataNode(recState.StartTime.ToString());

            yamlMetadata[MetaKeyIsClientRecording] = new ValueDataNode(_netMan.IsClient.ToString());

            RecordingStarted?.Invoke(yamlMetadata, extraData);

            var document = new YamlDocument(yamlMetadata.ToYaml());
            WriteYaml(recState, ReplayZipFolder / FileMeta, document);
        }

        // Saving misc extra data like networked messages that typically get sent to newly connecting clients.
        // TODO REPLAYS compression
        // currently resource uploads are uncompressed, so this might be quite big.
        if (extraData.Count > 0)
            WriteSerializer(recState, ReplayZipFolder / FileInit, new ReplayMessage { Messages = extraData });

        // save data required for IRobustMappedStringSerializer
        WriteBytes(recState, ReplayZipFolder / FileStrings, stringData, CompressionLevel.NoCompression);

        // Save replicated cvars.
        var cvars = NetConf.GetReplicatedVars(true).Select(x => x.name);
        WriteToml(recState, cvars, ReplayZipFolder / FileCvars);
    }

    private void WriteFinalMetadata(RecordingState recState)
    {
        var yamlMetadata = new MappingDataNode();
        RecordingStopped?.Invoke(yamlMetadata);
        var time = Timing.CurTime - recState.StartTime;
        yamlMetadata[MetaFinalKeyEndTick] = new ValueDataNode(Timing.CurTick.Value.ToString());
        yamlMetadata[MetaFinalKeyDuration] = new ValueDataNode(time.ToString());
        yamlMetadata[MetaFinalKeyFileCount] = new ValueDataNode(recState.Index.ToString());
        yamlMetadata[MetaFinalKeyCompressedSize] = new ValueDataNode(recState.CompressedSize.ToString());
        yamlMetadata[MetaFinalKeyUncompressedSize] = new ValueDataNode(recState.UncompressedSize.ToString());
        yamlMetadata[MetaFinalKeyEndTime] = new ValueDataNode(Timing.CurTime.ToString());

        // this just overwrites the previous yml with additional data.
        var document = new YamlDocument(yamlMetadata.ToYaml());
        WriteYaml(recState, ReplayZipFolder / FileMetaFinal, document);
        UpdateWriteTasks();
        RecordingFinished?.Invoke(recState.DestDir, recState.DestPath);
        Reset();
    }

    private void WriteContentBundleInfo(RecordingState recState)
    {
        if (!NetConf.GetCVar(CVars.ReplayMakeContentBundle))
            return;

        if (GetServerBuildInformation() is not { } info)
        {
            _sawmill.Warning("Missing necessary build information, replay will not be a launcher-runnable content bundle");
            return;
        }

        var document = new JsonObject
        {
            ["engine_version"] = info.EngineVersion,
            ["base_build"] = new JsonObject
            {
                ["fork_id"] = info.ForkId,
                ["version"] = info.Version,
                ["download_url"] = info.ZipDownload,
                ["hash"] = info.ZipHash,
                ["manifest_download_url"] = info.ManifestDownloadUrl,
                ["manifest_url"] = info.ManifestUrl,
                ["manifest_hash"] = info.ManifestHash
            }
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        WriteBytes(recState, new ResPath("rt_content_bundle.json"), bytes);
    }

    /// <summary>
    /// Get information describing the server build.
    /// This will be embedded in replay content bundles to allow the launcher to directly load them.
    /// </summary>
    /// <returns>null if we do not have build information.</returns>
    protected GameBuildInformation? GetServerBuildInformation()
    {
        var info = GameBuildInformation.GetBuildInfoFromConfig(NetConf);

        var zip = info.ZipDownload != null && info.ZipHash != null;
        var manifest = info.ManifestHash != null && info.ManifestUrl != null && info.ManifestDownloadUrl != null;

        if (!zip && !manifest)
        {
            // Don't have necessary info to write useful build info to the replay file.
            return null;
        }

        return info;
    }

    public (float Minutes, int Ticks, float Size, float UncompressedSize) GetReplayStats()
    {
        if (_recState == null)
            throw new InvalidOperationException("Not recording replay!");

        var time = (Timing.CurTime - _recState.StartTime).TotalMinutes;
        var tick = Timing.CurTick.Value - _recState.StartTick.Value;
        var size = _recState.CompressedSize / (1024f * 1024f);
        var altSize = _recState.UncompressedSize / (1024f * 1024f);

        return ((float)time, (int)tick, size, altSize);
    }

    /// <summary>
    /// Contains all state related to an active recording.
    /// </summary>
    private sealed class RecordingState
    {
        public readonly ZipArchive Zip;
        public readonly MemoryStream Buffer;
        public readonly ZStdCompressionContext CompressionContext;
        public readonly ChannelWriter<Action> WriteCommandChannel;
        public readonly Task WriteTask;
        public readonly IWritableDirProvider DestDir;
        public readonly ResPath DestPath;

        // Tick and time when the recording was started.
        public readonly GameTick StartTick;
        public readonly TimeSpan StartTime;

        // Optionally, the time the recording should automatically end at.
        public readonly TimeSpan? EndTime;

        public int Index;
        public int CompressedSize;
        public int UncompressedSize;

        public RecordingState(
            ZipArchive zip,
            MemoryStream buffer,
            ZStdCompressionContext compressionContext,
            GameTick startTick,
            TimeSpan startTime,
            TimeSpan? endTime,
            ChannelWriter<Action> writeCommandChannel,
            Task writeTask,
            IWritableDirProvider destDir,
            ResPath destPath)
        {
            WriteTask = writeTask;
            DestDir = destDir;
            DestPath = destPath;
            Zip = zip;
            Buffer = buffer;
            CompressionContext = compressionContext;
            StartTick = startTick;
            StartTime = startTime;
            EndTime = endTime;
            WriteCommandChannel = writeCommandChannel;
        }
    }
}
