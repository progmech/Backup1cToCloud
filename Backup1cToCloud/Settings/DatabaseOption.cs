using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Backup1cToCloud.Settings
{
    public class DatabaseOption
    {
        public string DatabasePath { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public string BackupName { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }
}
