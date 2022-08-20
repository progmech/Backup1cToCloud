using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Backup1cToCloud.Settings
{
    public class BackupOptions
    {
        public const string BackupOptionsName = "Backup";
        public int ArchiveDepthInDays { get; init; }
        public string BucketName { get; init; } = string.Empty;
        public string AccessKey { get; init; } = string.Empty;
        public string SecretKey { get; init; } = string.Empty;
        public string ServiceUrl { get; init; } = string.Empty;
        public EmailOptions EmailOptions { get; init; } = new EmailOptions();
        public List<DatabaseOption> Databases { get; } = new List<DatabaseOption>();
    }
}