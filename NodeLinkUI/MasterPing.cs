using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

namespace NodeLinkUI
{
    public static class MasterPing
    {
        public static bool IsMasterOnline(string masterIp)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(masterIp, 1000); // 1-second timeout
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }
    }
}

