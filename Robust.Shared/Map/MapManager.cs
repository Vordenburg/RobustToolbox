using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Shared.Map;

/// <inheritdoc cref="IMapManager" />
[Virtual]
internal partial class MapManager : IMapManagerInternal, IEntityEventSubscriber
{
    [field: Dependency] public IGameTiming GameTiming { get; } = default!;
    [field: Dependency] public IEntityManager EntityManager { get; } = default!;

    [Dependency] private readonly IConsoleHost _conhost = default!;

    private ISawmill _sawmill = default!;

    /// <inheritdoc />
    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("map");

#if DEBUG
        DebugTools.Assert(!_dbgGuardInit);
        DebugTools.Assert(!_dbgGuardRunning);
        _dbgGuardInit = true;
#endif
        InitializeMapPausing();
    }

    /// <inheritdoc />
    public void Startup()
    {
#if DEBUG
        DebugTools.Assert(_dbgGuardInit);
        _dbgGuardRunning = true;
#endif

        _sawmill.Debug("Starting...");
    }

    /// <inheritdoc />
    public void Shutdown()
    {
#if DEBUG
        DebugTools.Assert(_dbgGuardInit);
#endif
        _sawmill.Debug("Stopping...");

        foreach (var mapComp in EntityManager.EntityQuery<MapComponent>())
        {
            EntityManager.DeleteEntity(mapComp.Owner);
        }
    }

    /// <inheritdoc />
    public void Restart()
    {
        _sawmill.Debug("Restarting...");

        // Don't just call Shutdown / Startup because we don't want to touch the subscriptions on gridtrees
        // Restart can be called any time during a game, whereas shutdown / startup are typically called upon connection.
        foreach (var mapComp in EntityManager.EntityQuery<MapComponent>())
        {
            EntityManager.DeleteEntity(mapComp.Owner);
        }
    }

#if DEBUG
    private bool _dbgGuardInit;
    private bool _dbgGuardRunning;
#endif
}
