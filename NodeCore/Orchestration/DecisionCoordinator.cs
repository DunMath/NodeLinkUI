// NodeCore/Orchestration/DecisionCoordinator.cs
using System;
using System.Collections.Generic;

namespace NodeCore.Orchestration
{
    /// <summary>
    /// Headless decision layer. For now: pass-through to the remote orchestrator.
    /// Later: choose Local (LocalExecutor) vs Remote (_orchestrator) per TaskUnit.
    /// </summary>
    public sealed class DecisionCoordinator
    {
        private readonly Func<List<AgentStatus>> _snapshotAgents;
        private readonly DefaultOrchestrator _orchestrator;

        public DecisionCoordinator(
            Func<List<AgentStatus>> snapshotAgents,
            DefaultOrchestrator orchestrator)
        {
            _snapshotAgents = snapshotAgents ?? throw new ArgumentNullException(nameof(snapshotAgents));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        }

        /// <summary>
        /// Submit a batch of TaskUnits. Today this is a straight pass-through.
        /// </summary>
        public void Submit(string app, IReadOnlyList<TaskUnit> units)
        {
            _orchestrator.Submit(app, units);
        }
    }
}


