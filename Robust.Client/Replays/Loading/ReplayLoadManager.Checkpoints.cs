using System;
using System.Collections.Generic;
using System.Linq;
using NetSerializer;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Threading.Tasks;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Replays;
using Robust.Shared.Upload;
using static Robust.Shared.Replays.ReplayMessage;

namespace Robust.Client.Replays.Loading;

// This partial class contains functions for generating "checkpoint" states, which are basically just full states that
// allow the client to jump to some point in time without having to re-process the whole replay up to that point. I.e.,
// so that when jumping to tick 1001 the client only has to apply states for tick 1000 and 1001, instead of 0, 1, 2, ...
public sealed partial class ReplayLoadManager
{
    public async Task<(CheckpointState[], TimeSpan[])> GenerateCheckpointsAsync(
        ReplayMessage? initMessages,
        HashSet<string> initialCvars,
        List<GameState> states,
        List<ReplayMessage> messages,
        LoadReplayCallback callback)
    {
        // Given a set of states [0 to X], [X to X+1], [X+1 to X+2]..., this method  will generate additional states
        // like [0 to x+60 ], [0 to x+120], etc. This will make scrubbing/jumping to a state much faster, but requires
        // some pre-processing all of the states.
        //
        // This whole mess of a function uses a painful amount of LINQ conversion. but sadly the networked data is
        // generally sent as a list of values, which makes sense if the list contains simple state delta data that all
        // needs to be applied. But here we need to inspect existing states and combine/merge them, so things generally
        // need to be converted into a dictionary. But even with that requirement there are a bunch of performance
        // improvements to be made even without just de-LINQuifing or changing the networked data.
        //
        // Profiling with a 10 minute, 80-player replay, this function is about 50% entity spawning and 50% MergeState()
        // & array copying. It only takes ~3 seconds on my machine, so optimising it might not be necessary, but there
        // is still some low-hanging fruit, like:
        // TODO REPLAYS serialize checkpoints after first loading a replay so they only need to be generated once.
        //
        // TODO REPLAYS Add dynamic checkpoints.
        // If we end up using long (e.g., 5 minute) checkpoint intervals, that might still mean that scrubbing/rewinding
        // short time periods will be super stuttery. So its probably worth keeping a dynamic checkpoint following the
        // users current tick. E.g. while a replay is being replayed, keep a dynamic checkpoint that is ~30 secs behind
        // the current tick. that way the user can always go back up to ~30 seconds without having to go back to the
        // last checkpoint.
        //
        // Alternatively maybe just generate reverse states? I.e. states containing data that is required to go from
        // tick X to X-1? (currently any ent that had any changes will reset ALL of its components, not just the states
        // that actually need resetting. basically: iterate forwards though states. anytime a new  comp state gets
        // applied, for the reverse state simply add the previously applied component state.

        _sawmill.Info($"Begin checkpoint generation");
        var st = new Stopwatch();
        st.Start();

        Dictionary<string, object> cvars = new();
        foreach (var cvar in initialCvars)
        {
            cvars[cvar] = _confMan.GetCVar<object>(cvar);
        }

        var timeBase = _timing.TimeBase;
        var checkPoints = new List<CheckpointState>(1 + states.Count / _checkpointInterval);
        var state0 = states[0];

        // Get all initial prototypes
        var prototypes = new Dictionary<Type, HashSet<string>>();
        foreach (var kindName in _protoMan.GetPrototypeKinds())
        {
            var kind = _protoMan.GetKindType(kindName);
            var set = new HashSet<string>();
            prototypes[kind] = set;
            foreach (var proto in _protoMan.EnumeratePrototypes(kind))
            {
                set.Add(proto.ID);
            }
        }

        HashSet<ResPath> uploadedFiles = new();
        var detached = new HashSet<EntityUid>();
        var detachQueue = new Dictionary<GameTick, List<EntityUid>>();

        if (initMessages != null)
            UpdateMessages(initMessages, uploadedFiles, prototypes, cvars, detachQueue, ref timeBase, true);
        UpdateMessages(messages[0], uploadedFiles, prototypes, cvars, detachQueue, ref timeBase, true);
        ProcessQueue(GameTick.MaxValue, detachQueue, detached);

        var entSpan = state0.EntityStates.Value;
        Dictionary<EntityUid, EntityState> entStates = new(entSpan.Count);
        foreach (var entState in entSpan)
        {
            var modifiedState = AddImplicitData(entState);
            entStates.Add(entState.Uid, modifiedState);
        }

        await callback(0, states.Count, LoadingState.ProcessingFiles, true);
        var playerSpan = state0.PlayerStates.Value;
        Dictionary<NetUserId, PlayerState> playerStates = new(playerSpan.Count);
        foreach (var player in playerSpan)
        {
            playerStates.Add(player.UserId, player);
        }

        state0 = new GameState(GameTick.Zero,
            state0.ToSequence,
            default,
            entStates.Values.ToArray(),
            playerStates.Values.ToArray(),
            Array.Empty<EntityUid>());
        checkPoints.Add(new CheckpointState(state0, timeBase, cvars, 0, detached));

        DebugTools.Assert(state0.EntityDeletions.Value.Count == 0);
        var empty = Array.Empty<EntityUid>();

        TimeSpan GetTime(GameTick tick)
        {
            var rate = (int) cvars[CVars.NetTickrate.Name];
            var period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / rate);
            return timeBase.Item1 + (tick.Value - timeBase.Item2.Value) * period;
        }

        var serverTime = new TimeSpan[states.Count];
        serverTime[0] = TimeSpan.Zero;
        var initialTime = GetTime(state0.ToSequence);

        var ticksSinceLastCheckpoint = 0;
        var spawnedTracker = 0;
        var stateTracker = 0;
        for (var i = 1; i < states.Count; i++)
        {
            if (i % 10 == 0)
                await callback(i, states.Count, LoadingState.ProcessingFiles, false);

            var curState = states[i];
            UpdatePlayerStates(curState.PlayerStates.Span, playerStates);
            UpdateEntityStates(curState.EntityStates.Span, entStates, ref spawnedTracker, ref stateTracker, detached);
            UpdateMessages(messages[i], uploadedFiles, prototypes, cvars, detachQueue, ref timeBase);
            ProcessQueue(curState.ToSequence, detachQueue, detached);
            UpdateDeletions(curState.EntityDeletions, entStates, detached);
            serverTime[i] = GetTime(curState.ToSequence) - initialTime;
            ticksSinceLastCheckpoint++;

            if (ticksSinceLastCheckpoint < _checkpointInterval
                && spawnedTracker < _checkpointEntitySpawnThreshold
                && stateTracker < _checkpointEntityStateThreshold)
            {
                continue;
            }

            ticksSinceLastCheckpoint = 0;
            spawnedTracker = 0;
            stateTracker = 0;
            var newState = new GameState(GameTick.Zero,
                curState.ToSequence,
                default,
                entStates.Values.ToArray(),
                playerStates.Values.ToArray(),
                empty); // for full states, deletions are implicit by simply not being in the state
            checkPoints.Add(new CheckpointState(newState, timeBase, cvars, i, detached));
        }

        _sawmill.Info($"Finished generating checkpoints. Elapsed time: {st.Elapsed}");
        await callback(states.Count, states.Count, LoadingState.ProcessingFiles, false);
        return (checkPoints.ToArray(), serverTime);
    }

    private void ProcessQueue(
        GameTick curTick,
        Dictionary<GameTick, List<EntityUid>> detachQueue,
        HashSet<EntityUid> detached)
    {
        foreach (var (tick, ents) in detachQueue)
        {
            if (tick > curTick)
                continue;
            detachQueue.Remove(tick);
            detached.UnionWith(ents);
        }
    }

    private void UpdateMessages(ReplayMessage message,
        HashSet<ResPath> uploadedFiles,
        Dictionary<Type, HashSet<string>> prototypes,
        Dictionary<string, object> cvars,
        Dictionary<GameTick, List<EntityUid>> detachQueue,
        ref (TimeSpan, GameTick) timeBase,
        bool ignoreDuplicates = false)
    {
        for (var i = message.Messages.Count - 1; i >= 0; i--)
        {
            switch (message.Messages[i])
            {
                case CvarChangeMsg cvar:
                    foreach (var (name, value) in cvar.ReplicatedCvars)
                    {
                        cvars[name] = value;
                    }

                    timeBase = cvar.TimeBase;
                    break;

                case SharedNetworkResourceManager.ReplayResourceUploadMsg resUpload:

                    var path = resUpload.RelativePath.Clean().ToRelativePath();
                    if (uploadedFiles.Add(path) && !_netResMan.FileExists(path))
                    {
                        _netMan.DispatchLocalNetMessage(new NetworkResourceUploadMessage
                        {
                            RelativePath = path, Data = resUpload.Data
                        });
                        message.Messages.RemoveSwap(i);
                        break;
                    }

                    // Supporting this requires allowing files to track their last-modified time and making
                    // checkpoints reset files when jumping back, and applying all previous changes when jumping
                    // forwards. Also, note that files HAVE to be uploaded while generating checkpoints, in case
                    // someone spawns an entity that relies on uploaded data.
                    if (!ignoreDuplicates)
                        throw new NotSupportedException("Overwriting an existing file is not yet supported by replays.");

                    message.Messages.RemoveSwap(i);
                    break;

                case LeavePvs leave:
                    detachQueue.TryAdd(leave.Tick, leave.Entities);
                    break;
            }
        }

        // Process prototype uploads **after** resource uploads.
        for (var i = message.Messages.Count - 1; i >= 0; i--)
        {
            if (message.Messages[i] is not ReplayPrototypeUploadMsg protoUpload)
                continue;

            message.Messages.RemoveSwap(i);
            var changed = new Dictionary<Type, HashSet<string>>();
            _protoMan.LoadString(protoUpload.PrototypeData, true, changed);

            foreach (var (kind, ids) in changed)
            {
                var protos = prototypes[kind];
                var count = protos.Count;
                protos.UnionWith(ids);
                if (!ignoreDuplicates && ids.Count + count != protos.Count)
                {
                    // An existing prototype was overwritten. Much like for resource uploading, supporting this
                    // requires tracking the last-modified time of prototypes and either resetting or applying
                    // prototype changes when jumping around in time. This also requires reworking how the initial
                    // implicit state data is generated, because we can't simply cache it anymore.
                    // Also, does reloading prototypes in release mode modify existing entities?
                    throw new NotSupportedException($"Overwriting an existing prototype is not yet supported by replays.");
                }
            }

            _protoMan.ResolveResults();
            _protoMan.ReloadPrototypes(changed);
            _locMan.ReloadLocalizations();
        }
    }

    private void UpdateDeletions(NetListAsArray<EntityUid> entityDeletions,
        Dictionary<EntityUid, EntityState> entStates, HashSet<EntityUid> detached)
    {
        foreach (var ent in entityDeletions.Span)
        {
            entStates.Remove(ent);
            detached.Remove(ent);
        }
    }

    private void UpdateEntityStates(ReadOnlySpan<EntityState> span, Dictionary<EntityUid, EntityState> entStates,
        ref int spawnedTracker, ref int stateTracker, HashSet<EntityUid> detached)
    {
        foreach (var entState in span)
        {
            detached.Remove(entState.Uid);
            if (!entStates.TryGetValue(entState.Uid, out var oldEntState))
            {
                var modifiedState = AddImplicitData(entState);
                entStates[entState.Uid] = modifiedState;
                spawnedTracker++;

#if DEBUG
                foreach (var state in modifiedState.ComponentChanges.Value)
                {
                    DebugTools.Assert(state.State is not IComponentDeltaState delta || delta.FullState);
                }
#endif
                continue;
            }

            stateTracker++;
            DebugTools.Assert(oldEntState.Uid == entState.Uid);
            entStates[entState.Uid] = MergeStates(entState, oldEntState.ComponentChanges.Value, oldEntState.NetComponents);

#if DEBUG
            foreach (var state in entStates[entState.Uid].ComponentChanges.Span)
            {
                DebugTools.Assert(state.State is not IComponentDeltaState delta || delta.FullState);
            }
#endif
        }
    }

    private EntityState MergeStates(
        EntityState newState,
        IReadOnlyCollection<ComponentChange> oldState,
        HashSet<ushort>? oldNetComps)
    {
        var combined = oldState.ToList();
        var newCompStates = newState.ComponentChanges.Value.ToDictionary(x => x.NetID);

        // remove any deleted components
        if (newState.NetComponents != null)
        {
            for (var index = combined.Count - 1; index >= 0; index--)
            {
                if (!newState.NetComponents.Contains(combined[index].NetID))
                    combined.RemoveSwap(index);
            }
        }

        for (var index = combined.Count - 1; index >= 0; index--)
        {
            var existing = combined[index];

            if (!newCompStates.Remove(existing.NetID, out var newCompState))
                continue;

            if (newCompState.State is not IComponentDeltaState delta || delta.FullState)
            {
                combined[index] = newCompState;
                continue;
            }

            DebugTools.Assert(existing.State is IComponentDeltaState fullDelta && fullDelta.FullState);
            combined[index] = new ComponentChange(existing.NetID, delta.CreateNewFullState(existing.State), newCompState.LastModifiedTick);
        }

        foreach (var compChange in newCompStates.Values)
        {
            // I'm not 100% sure about this, but I think delta states should always be full states here?
            DebugTools.Assert(compChange.State is not IComponentDeltaState delta || delta.FullState);
            combined.Add(compChange);
        }

        DebugTools.Assert(newState.NetComponents == null || newState.NetComponents.Count == combined.Count);
        return new EntityState(newState.Uid, combined, newState.EntityLastModified, newState.NetComponents ?? oldNetComps);
    }

    private void UpdatePlayerStates(ReadOnlySpan<PlayerState> span, Dictionary<NetUserId, PlayerState> playerStates)
    {
        foreach (var player in span)
        {
            playerStates[player.UserId] = player;
        }
    }
}
