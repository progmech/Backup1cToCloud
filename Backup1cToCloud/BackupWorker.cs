using Amazon.S3;
using Amazon.S3.Model;
using Backup1cToCloud.Settings;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Net.Mail;
using System.Text;

namespace Backup1cToCloud
{
    public class BackupWorker : BackgroundService
    {
        private const int HoursInADay = 24;
        private readonly ILogger<BackupWorker>? _logger;
        private readonly IOptions<BackupOptions>? _config;
        private readonly AmazonS3Client? _s3client;

        public BackupWorker(ILogger<BackupWorker> logger, IOptions<BackupOptions> config)
        {
            try 
            {
                _logger = logger;
                _config = config;
                AmazonS3Config configsS3 = new AmazonS3Config
                {
                    ServiceURL = _config.Value.ServiceURL
                };

                _s3client = new AmazonS3Client(_config.Value.AccessKey, _config.Value.SecretKey, configsS3);
            }
            catch (Exception ex)
            {
                SendMail($"Ошибка инициализации в конструкторе: {ex.Message}", config.Value.EmailOptions);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //while (!stoppingToken.IsCancellationRequested)
            //{
                var startDate = DateTime.Now;
                _logger.LogInformation("Резервное копирование начато {Time}.", startDate);
                foreach (DatabaseOption databaseOption in _config.Value.Databases) 
                {
                    try 
                    {
                        Backup(databaseOption);
                        Cleanup(databaseOption);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("{Message}", ex.Message);
                        SendMail(ex.Message, _config.Value.EmailOptions);
                    }
                }
                var endDate = DateTime.Now;
                // var delayHours = TimeSpan.FromHours(HoursInADay - (endDate - startDate).TotalHours);
                _logger.LogInformation("Резервное копирование закончено {Time}.", endDate);
            //    _logger.LogInformation("Следующий запуск {Time}.", endDate + delayHours);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            //}
        }

        private void Backup(DatabaseOption databaseOption)
        {
            CheckPathsExist(databaseOption, out string copyFrom, out string copyTo, out string archiveTo);
            CheckBucketExists(_config.Value.BucketName);
            CheckEmailSettings(_config.Value.EmailOptions);
            CopyDatabaseToFolder(copyFrom, copyTo);
            string archiveName = ArchiveDatabaseToFolder(copyTo, archiveTo, databaseOption.DatabaseName);
            CopyArchiveToCloud(_config.Value.BucketName, archiveName);
            CompareChecksum(_config.Value.BucketName, archiveName);
        }

        private void CheckEmailSettings(EmailOptions options)
        {
            if (string.IsNullOrEmpty(options.SmtpServer)
                || string.IsNullOrEmpty(options.From)
                || string.IsNullOrEmpty(options.To)
                || string.IsNullOrEmpty(options.UserName)
                || string.IsNullOrEmpty(options.Password)) {
                    throw new Exception("Неправильные настройки электронной почты!");
                }
        }

        private void CompareChecksum(string bucketName, string archiveName)
        {
            string sourceCheckSum, targetCheckSum;
            using (FileStream fop = File.OpenRead(archiveName))
            {
                sourceCheckSum = BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(fop)).Replace("-", string.Empty);
            }
            Task<ListObjectsResponse> files = ListObjectsAsync(bucketName);
            foreach (var obj in files.Result.S3Objects) 
            {
                if(obj.Key == Path.GetFileName(archiveName)) 
                {
                    targetCheckSum = obj.ETag.ToUpper().Replace("\"", string.Empty);
                    if(sourceCheckSum != targetCheckSum) 
                    {
                        throw new Exception($"Контрольные суммы файла {archiveName} и файла {obj.Key} в облаке {bucketName} не совпадают!");
                    }
                }
            }
        }

        private void Cleanup(DatabaseOption databaseOption)
        {
            CleanupFolder(databaseOption);
            CleanupCloud(_config.Value.BucketName, databaseOption.BackupName);
        }

        private void CleanupFolder(DatabaseOption option)
        {
            string fullFileName = Path.Combine(option.BackupPath, option.BackupName);
            foreach (var file in Directory. EnumerateFiles(option.BackupPath).Where(f => f.StartsWith(fullFileName)))
            {
                try
                {
                    if (File.GetCreationTime(file) < DateTime.Today.AddDays(-1 * _config.Value.ArchiveDepthInDays))
                    {
                        File.Delete(file);
                        _logger.LogInformation("Файл {File} удалён из папки {BackupPath}.", file, option.BackupPath);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Ошибка {ex.Message} при удалении файла {file}!");
                }
            }
        }

        private void CleanupCloud(string bucketName, string fileName)
        {
            Task<ListObjectsResponse> files = ListObjectsAsync(bucketName);
            foreach (var obj in files.Result.S3Objects)
            {
                if (obj.Key.StartsWith(fileName) && obj.LastModified < DateTime.Today.AddDays(-1 * _config.Value.ArchiveDepthInDays)) {
                    var response = DeleteObjectAsync(bucketName, obj.Key);
                    if (response.Result.HttpStatusCode == System.Net.HttpStatusCode.NoContent && response.IsCompletedSuccessfully)
                    {
                        _logger.LogInformation("Файл {Key} удалён из хранилища {Name}", obj.Key, bucketName);
                    }
                    else 
                    {
                        throw new Exception($"Ошибка при удалении файла {obj.Key} из хранилища {bucketName}");
                    }
                }
            }
        }

        private async Task<ListObjectsResponse> ListObjectsAsync(string bucketName)
        {
            ListObjectsRequest request = new ListObjectsRequest()
            {
                BucketName = bucketName
            };
            var response = await _s3client.ListObjectsAsync(request);
            return response;
        }

        private async Task<DeleteObjectResponse> DeleteObjectAsync(string bucketName, string key)
        {
            var response = await _s3client.DeleteObjectAsync(bucketName, key);
            return response;
        }

        private void CheckBucketExists(string bucketName)
        {
            Task<ListBucketsResponse> response = ListBucketsAsync();
            if(!response.Result.Buckets.Any(b => b.BucketName == bucketName)) {
                throw new Exception($"Каталога {bucketName} не существует в облаке!");
            }
        }

        private async Task<ListBucketsResponse> ListBucketsAsync()
        {
            var response = await _s3client.ListBucketsAsync();
            return response;
        }

        private void CopyArchiveToCloud(string bucketName, string archiveName)
        {
            Task<PutObjectResponse> putResponse = PutObjectAsync(bucketName, archiveName);
            if (putResponse.Result.HttpStatusCode == System.Net.HttpStatusCode.OK && putResponse.IsCompletedSuccessfully)
            {
                _logger.LogInformation("Файл {Archive} скопирован в хранилище {Bucket}", archiveName, bucketName);
            }
            else
            {
                throw new Exception($"Ошибка при копировании файла {archiveName} в хранилище {bucketName}");
            }
        }

        private async Task<PutObjectResponse> PutObjectAsync(string bucketName, string archiveName)
        {
            string chksum;
            using (FileStream fop = File.OpenRead(archiveName))
            {
                chksum = BitConverter.ToString(System.Security.Cryptography.SHA1.Create().ComputeHash(fop));
            }
            PutObjectRequest request = new()
            {
                BucketName = bucketName,
                Key = Path.GetFileName(archiveName),
                UseChunkEncoding = false,
                FilePath = archiveName,
                ChecksumAlgorithm = ChecksumAlgorithm.SHA1,
                ChecksumSHA1 = chksum + 5
            };
            var response = await _s3client.PutObjectAsync(request);
            return response;
        }

        private void CheckPathsExist(DatabaseOption databaseOption, out string copyFrom, out string copyTo, out string archivePath)
        {
            if (string.IsNullOrEmpty(databaseOption.DatabasePath))
            {
                throw (new Exception("Путь папки файла базы данных не задан!"));
            }
            if (string.IsNullOrEmpty(databaseOption.DatabaseName))
            {
                throw new Exception("Имя файла базы данных не задано!");
            }
            if (string.IsNullOrEmpty(databaseOption.BackupPath))
            {
                throw new Exception("Путь папки резервного копирования не задан!");
            }
            if (string.IsNullOrEmpty(databaseOption.BackupName))
            {
                throw new Exception("Имя файла архива не задано");
            }
            copyFrom = Path.Combine(databaseOption.DatabasePath, databaseOption.DatabaseName);
            copyTo = Path.Combine(databaseOption.BackupPath, databaseOption.DatabaseName);
            archivePath = Path.Combine(databaseOption.BackupPath, databaseOption.BackupName);
        }

        private void CopyDatabaseToFolder(string copyFrom, string copyTo) 
        {
            if(!File.Exists(copyFrom)) {
                throw (new Exception($"Файл базы данных {copyFrom} не существует!"));
            }
            DateTime copyToTime = File.GetCreationTime(copyTo);
            DateTime copyFromTime = File.GetCreationTime(copyFrom);
            if (File.Exists(copyTo) && copyToTime != copyFromTime)
            {
                File.Delete(copyTo);
                _logger.LogInformation("Файл {CopyTo} с датой создания {CopyToTime} удалён.", copyTo, copyToTime);
            }
            File.Copy(copyFrom, copyTo);
            _logger.LogInformation("База данных {CopyFrom} скопирована в {CopyTo}.", copyFrom, copyTo);
        }

        private string ArchiveDatabaseToFolder(string sourcePath, string archiveTo, string fileName) 
        {
            DateTime today = DateTime.Today;
            var archivePath = $"{archiveTo}-{today:yyyy}{today:MM}{today:dd}.zip";
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile (sourcePath, fileName);
            }
            _logger.LogInformation("База данных {SourcePath} заархивирована в {ArchivePath}.", sourcePath, archivePath);
            return archivePath;
        }

        private void SendMail(string body, EmailOptions options) {
            try
            {
                SmtpClient smtp = new(options.SmtpServer, options.Port);
                using MailMessage message = new();
                Encoding encoding = Encoding.UTF8;
                message.IsBodyHtml = false;
                message.SubjectEncoding = encoding;
                message.BodyEncoding = encoding;
                message.From = new MailAddress(options.From, options.From, encoding);
                message.Bcc.Add(new MailAddress(options.To, options.To, encoding));
                message.Subject = "Отчёт о сохранении баз в облако";
                message.Body = body;
                smtp.EnableSsl = true;
                smtp.Credentials = new System.Net.NetworkCredential(options.UserName, options.Password);
                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtp.Send(message);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка {ex.Message} при отправке письма с сервера {options.SmtpServer}, порт {options.Port}");
            }
        }
    }
}