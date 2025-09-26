using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NodeCore
{
    public class TaskInfo
    {
        public string TaskId { get; set; }
        public string Description { get; set; }
        public string AgentId { get; set; }
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            return $"{Timestamp:T}: {Description} on Agent: {AgentId}";
        }
    }
}
