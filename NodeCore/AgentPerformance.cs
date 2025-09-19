using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NodeCore
{
    namespace NodeCore
    {
        public class AgentPerformance
        {
            public string AgentId { get; set; } = "";
            public float CpuLoadPercent { get; set; }
            public float GpuLoadPercent { get; set; }
            public int RamFreeMB { get; set; }
            public int TaskQueueLength { get; set; }
            public TimeSpan AvgTaskDuration { get; set; }
            public DateTime LastHeartbeat { get; set; }
            public bool IsResponsive => (DateTime.Now - LastHeartbeat).TotalSeconds < 10;
        }
    }
}

