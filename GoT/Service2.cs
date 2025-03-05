using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
//using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.ComponentModel;
using System.Configuration.Install;
using Serilog;
using Serilog.Events;
namespace GoT
{
    public partial class Service2 : ServiceBase
    {
        private static ILogger _logger;
        private static IConfiguration _configuration;
        private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private static string _defaultBrandName;
        public Service2()
        {
            ServiceName = "Go_tenant";
            InitializeComponent();
        }
        protected override void OnStart(string[] args)
        {
            Task.Run(async () => await StartServiceAsync());
        }
        protected override void OnStop()
        {
        }

        private async Task StartServiceAsync()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            _configuration = configuration;

            string staticLogFilePath = @"C:\Logs\MyService.log";

            // Ensure the log directory exists
            string logDirectory = Path.GetDirectoryName(staticLogFilePath);
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: staticLogFilePath,
                    rollingInterval: RollingInterval.Day, // Roll files daily
                    fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB file size limit
                    retainedFileCountLimit: 5, // Keep up to 5 rolled files
                    encoding: Encoding.UTF8, // Use UTF-8 encoding
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}" // Custom output template
                )
                .CreateLogger();

            _logger = Log.Logger;

            //var loggerFactory = LoggerFactory.Create(builder =>
            //{
            //    string logFilePath = _configuration["Logging:FilePath"];
            //    if (!string.IsNullOrEmpty(logFilePath) && !Path.IsPathRooted(logFilePath))
            //    {
            //        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFilePath);
            //    }

            //    if (!string.IsNullOrEmpty(logFilePath))
            //    {
            //        // Create a FileLoggerContext with the desired configuration
            //        var fileLoggerContext = new FileLoggerContext(logFilePath)
            //        {
            //            RotationSize = 10 * 1024 * 1024, // 10 MB
            //            RetainedFilesCountLimit = 5,     // Keep 5 rolled files
            //            Encoding = System.Text.Encoding.UTF8,
            //            OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}"
            //        };

            //        // Pass the FileLoggerContext to AddFile
            //        builder.AddFile(fileLoggerContext);
            //    }

            //    builder.AddConfiguration(_configuration.GetSection("Logging"));
            //});

            //_logger = loggerFactory.CreateLogger("Program");

            try
            {
                // Create handlers
                var mongoDBHandler = new MongoDBHandler(_configuration, _logger);
                var receiptProcessor = new ReceiptProcessor(_configuration, _logger);
                var frappeClient = new FrappeClient(_configuration, _logger, mongoDBHandler);

                // Get file path from configuration
                var path = _configuration.GetSection("Path");
                string logFilePath = path["file"];

                if (string.IsNullOrEmpty(logFilePath))
                {
                    _logger.Error("Log file path is not configured in appsettings.json");
                    return;
                }

                // Resolve the relative path to an absolute path
                if (!Path.IsPathRooted(logFilePath))
                {
                    logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFilePath);
                }

                string directory = Path.GetDirectoryName(logFilePath);
                string fileName = Path.GetFileName(logFilePath);

                // Ensure the log directory exists
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                // Declare debounce variables
                DateTime lastProcessedTime = DateTime.MinValue;
                TimeSpan debouncePeriod = TimeSpan.FromMilliseconds(500);
                // Set up file watcher
                
               
                FileSystemWatcher watcher = new FileSystemWatcher
                {
                    Path = "C:\\Logs",
                    Filter = "test.txt",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += async (sender, e) =>
                {
                    var now = DateTime.Now;
                    if (now - lastProcessedTime < debouncePeriod)
                    {
                        return; // Debounce - ignore frequent changes
                    }

                    lastProcessedTime = now;

                    try
                    {
                        await _fileLock.WaitAsync();
                        _logger.Information($"Processing changes to {e.FullPath}");

                        // Wait for file to be released
                        await Task.Delay(100);

                        // Process the file
                        var lines = File.ReadLines(e.FullPath).ToArray();
                        var receipts = receiptProcessor.ProcessLogLines(lines);

                        // Save receipts to MongoDB
                        foreach (var receipt in receipts)
                        {
                            receipt.BrandName = _defaultBrandName;
                            bool addedToMongo = mongoDBHandler.AddReceipt(receipt);
                            if (addedToMongo)
                            {
                                _logger.Information($"Added receipt {receipt.CheckNumber} to MongoDB");
                            }
                        }
                    }
                    catch (IOException ioEx)
                    {
                        _logger.Error($"File access error: {ioEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error processing file: {ex.Message}");
                    }
                    finally
                    {
                        _fileLock.Release();
                    }
                };

                _logger.Information($"Watching log file: {logFilePath}");

                // Periodically send unsent receipts to Frappe
                while (true)
                {
                    try
                    {
                        var unsentReceipts = await mongoDBHandler.GetUnsentReceipts();

                        if (unsentReceipts.Count > 0)
                        {
                            _logger.Information($"Found {unsentReceipts.Count} unsent receipts");

                            foreach (var receipt in unsentReceipts)
                            {
                                if (await frappeClient.SendReceiptAsync(receipt))
                                {
                                    await mongoDBHandler.MarkReceiptAsSent(new List<string> { receipt.Id });
                                }
                            }
                        }
                        else
                        {
                            _logger.Information("No unsent receipts found");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error processing receipts: {ex.Message}");
                    }

                    // Wait before checking again
                    await Task.Delay(TimeSpan.FromSeconds(60));
                }
            }
            catch (Exception ex)
            {
                _logger.Fatal($"Application startup failed: {ex.Message}");
            }
        }
    }

    public class MongoDBHandler
    {
        private readonly IMongoCollection<Receipt> _receiptCollection;
        private readonly ILogger _logger;

        public MongoDBHandler(IConfiguration configuration, ILogger logger)
        {
            _logger = logger;

            try
            {
                var mongoConfig = configuration.GetSection("MongoDB");

                var connectionString = mongoConfig["ConnectionString"];
                var databaseName = mongoConfig["DatabaseName"];
                var collectionName = mongoConfig["CollectionName"];

                if (string.IsNullOrEmpty(connectionString) ||
                    string.IsNullOrEmpty(databaseName) ||
                    string.IsNullOrEmpty(collectionName))
                {
                    throw new ArgumentException("MongoDB configuration is incomplete");
                }

                var settings = MongoClientSettings.FromConnectionString(connectionString);
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);

                var client = new MongoClient(settings);
                var database = client.GetDatabase(databaseName);
                _receiptCollection = database.GetCollection<Receipt>(collectionName);

                // Create index for faster lookups
                var indexKeysDefinition = Builders<Receipt>.IndexKeys
                    .Ascending(nameof(Receipt.ChkNumber))
                    .Ascending(nameof(Receipt.Date))
                    .Ascending(nameof(Receipt.Time));

                _receiptCollection.Indexes.CreateOne(
                    new CreateIndexModel<Receipt>(indexKeysDefinition,
                        new CreateIndexOptions { Unique = true, Background = true }));

                _logger.Information("MongoDB connection established successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize MongoDB: {ex.Message}");
                throw;
            }
        }

        public bool AddReceipt(Receipt receipt)
        {
            if (!ValidateReceipt(receipt))
            {
                _logger.Warning("Invalid receipt data: Missing required fields");
                return false;
            }

            receipt.Id = null; // Ensure _id is ignored when inserting a new document
            receipt.IsSent = 0; // Set is_sent field to 0 (new receipt)

            try
            {
                var filter = Builders<Receipt>.Filter.And(
                    Builders<Receipt>.Filter.Eq(nameof(Receipt.ChkNumber), receipt.ChkNumber),
                    Builders<Receipt>.Filter.Eq(nameof(Receipt.Date), receipt.Date),
                    Builders<Receipt>.Filter.Eq(nameof(Receipt.Time), receipt.Time));

                var existingReceipt = _receiptCollection.Find(filter).FirstOrDefault();

                if (existingReceipt == null)
                {
                    _receiptCollection.InsertOne(receipt);
                    _logger.Information($"Added receipt {receipt.CheckNumber} to MongoDB");
                    return true;
                }
                else
                {
                    _logger.Information($"Receipt {receipt.CheckNumber} already exists in MongoDB");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error adding receipt to MongoDB: {ex.Message}");
                return false;
            }
        }

        public async Task<List<Receipt>> GetUnsentReceipts(int limit = 100)
        {
            try
            {
                var filter = Builders<Receipt>.Filter.Eq(nameof(Receipt.IsSent), 0);
                return await _receiptCollection.Find(filter).Limit(limit).ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Error retrieving unsent receipts: {ex.Message}");
                return new List<Receipt>();
            }
        }

        public async Task MarkReceiptAsSent(List<string> receiptIds)
        {
            if (receiptIds == null || receiptIds.Count == 0)
                return;

            try
            {
                var objectIds = receiptIds.Select(id => new ObjectId(id)).ToList();
                var filter = Builders<Receipt>.Filter.In(nameof(Receipt.Id), objectIds);
                var update = Builders<Receipt>.Update.Set(nameof(Receipt.IsSent), 1);

                await _receiptCollection.UpdateManyAsync(filter, update);
                _logger.Information($"Marked {receiptIds.Count} receipts as sent");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error marking receipts as sent: {ex.Message}");
            }
        }

        private bool ValidateReceipt(Receipt receipt)
        {
            return receipt != null &&
                   !string.IsNullOrEmpty(receipt.ChkNumber) &&
                   !string.IsNullOrEmpty(receipt.Date) &&
                   !string.IsNullOrEmpty(receipt.EmployeeNumber);
        }
    }

    /// Processes log files to extract receipt information using configurable regex patterns
    public class ReceiptProcessor
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Regex> _regexPatterns;

        public ReceiptProcessor(IConfiguration configuration, ILogger logger)
        {
            _logger = logger;
            _regexPatterns = new Dictionary<string, Regex>();

            var regexConfig = configuration.GetSection("RegexPatterns");

            if (regexConfig.Exists())
            {
                try
                {
                    foreach (var pattern in regexConfig.GetChildren())
                    {
                        string patternName = pattern.Key;
                        string patternValue = pattern.Value;

                        if (!string.IsNullOrEmpty(patternValue))
                        {
                            _regexPatterns[patternName] = new Regex(patternValue, RegexOptions.Compiled);
                            _logger.Debug($"Loaded regex pattern '{patternName}': {patternValue}");
                        }
                    }

                    _logger.Information($"Loaded {_regexPatterns.Count} regex patterns from configuration");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error loading regex patterns: {ex.Message}");
                    LoadDefaultPatterns();
                }
            }
            else
            {
                _logger.Warning("No RegexPatterns section found in configuration, using defaults");
                LoadDefaultPatterns();
            }
        }

        private void LoadDefaultPatterns()
        {
            _regexPatterns["CheckNumber"] = new Regex(@"Chk:\s*(\d{6})", RegexOptions.Compiled);
            _regexPatterns["DateTime"] = new Regex(@"(\d{2}/\d{2}/\d{4}) (\d{2}:\d{2} [apm]{2})", RegexOptions.Compiled);
            _regexPatterns["TableInfo"] = new Regex(@".*Tbl\s+(\d+\/\d+).*Chk\s+(\d+).*Gst\s+(\d+)", RegexOptions.Compiled);
            _regexPatterns["Subtotal"] = new Regex(@".*Subtotal\s+(\d+\.\d{2})", RegexOptions.Compiled);
            _regexPatterns["Tax"] = new Regex(@"\s*Tax\s+(\d+\.\d{2})", RegexOptions.Compiled);
            _regexPatterns["VAT"] = new Regex(@".*VAT\s+(\d+\.\d{2})", RegexOptions.Compiled);
            _regexPatterns["Payment"] = new Regex(@".*Chk:\s*(\d+).*Emp:\s*(\d+).*Payment\s+(\d+\.\d{2})", RegexOptions.Compiled);
            _regexPatterns["PrintTrigger"] = new Regex(@"PrintResponse|print -> Buffer", RegexOptions.Compiled);
            _regexPatterns["CloseTrigger"] = new Regex(@"Check Closed|-------", RegexOptions.Compiled);

            _logger.Information("Loaded default regex patterns");
        }

        public List<Receipt> ProcessLogLines(string[] lines)
        {
            var receiptsToPrint = new List<Receipt>();
            Receipt currentReceipt = null;
            bool inPrintBuffer = false;

            foreach (var line in lines)
            {
                try
                {
                    if (_regexPatterns.TryGetValue("PrintTrigger", out Regex printTriggerPattern) &&
                        printTriggerPattern.IsMatch(line))
                    {
                        inPrintBuffer = true;
                        currentReceipt = new Receipt();

                        if (_regexPatterns.TryGetValue("CheckNumber", out Regex checkNumberPattern))
                        {
                            var checkMatch = checkNumberPattern.Match(line);
                            if (checkMatch.Success)
                            {
                                currentReceipt.CheckNumber = checkMatch.Groups[1].Value;
                            }
                        }
                    }

                    if (currentReceipt != null)
                    {
                        ExtractReceiptData(line, currentReceipt);
                    }

                    if (inPrintBuffer && _regexPatterns.TryGetValue("CloseTrigger", out Regex closeTriggerPattern) &&
                        closeTriggerPattern.IsMatch(line))
                    {
                        inPrintBuffer = false;
                        if (IsValidReceipt(currentReceipt))
                        {
                            receiptsToPrint.Add(currentReceipt);
                            _logger.Debug($"Extracted receipt: {currentReceipt.CheckNumber} on {currentReceipt.Date}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error processing log line: {ex.Message}");
                }
            }

            _logger.Information($"Extracted {receiptsToPrint.Count} receipts from log file");
            return receiptsToPrint;
        }

        private void ExtractReceiptData(string line, Receipt receipt)
        {
            if (_regexPatterns.TryGetValue("DateTime", out Regex dateTimePattern))
            {
                var dateTimeMatch = dateTimePattern.Match(line);
                if (dateTimeMatch.Success)
                {
                    receipt.Date = dateTimeMatch.Groups[1].Value;
                    receipt.Time = dateTimeMatch.Groups[2].Value;
                }
            }

            if (_regexPatterns.TryGetValue("TableInfo", out Regex tableInfoPattern))
            {
                var tableMatch = tableInfoPattern.Match(line);
                if (tableMatch.Success)
                {
                    receipt.TableNumber = tableMatch.Groups[1].Value;
                    receipt.ChkNumber = tableMatch.Groups[2].Value;
                    receipt.GuestCount = tableMatch.Groups[3].Value;
                }
            }

            if (_regexPatterns.TryGetValue("Subtotal", out Regex subtotalPattern))
            {
                var subtotalMatch = subtotalPattern.Match(line);
                if (subtotalMatch.Success && decimal.TryParse(subtotalMatch.Groups[1].Value, out decimal subtotal))
                {
                    receipt.Subtotal = subtotal;
                }
            }

            if (_regexPatterns.TryGetValue("Tax", out Regex taxPattern))
            {
                var taxMatch = taxPattern.Match(line);
                if (taxMatch.Success && decimal.TryParse(taxMatch.Groups[1].Value, out decimal tax))
                {
                    receipt.Tax = tax;
                }
            }

            if (_regexPatterns.TryGetValue("VAT", out Regex vatPattern))
            {
                var vatMatch = vatPattern.Match(line);
                if (vatMatch.Success && decimal.TryParse(vatMatch.Groups[1].Value, out decimal vat))
                {
                    receipt.VAT = vat;
                }
            }

            if (_regexPatterns.TryGetValue("Payment", out Regex paymentPattern))
            {
                var paymentMatch = paymentPattern.Match(line);
                if (paymentMatch.Success)
                {
                    string checkNumber = paymentMatch.Groups[1].Value;
                    string employeeNumber = paymentMatch.Groups[2].Value;

                    if (checkNumber == receipt.CheckNumber && decimal.TryParse(paymentMatch.Groups[3].Value, out decimal payment))
                    {
                        receipt.Payment = payment;
                        receipt.EmployeeNumber = employeeNumber;
                    }
                }
            }
        }

        private bool IsValidReceipt(Receipt receipt)
        {
            return receipt != null &&
                   !string.IsNullOrEmpty(receipt.CheckNumber) &&
                   !string.IsNullOrEmpty(receipt.Date) &&
                   !string.IsNullOrEmpty(receipt.EmployeeNumber);
        }
    }

    /// Handles communication with Frappe site
    public class FrappeClient
    {
        private readonly string _baseUrl;
        private readonly string _apiEndpoint;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly ILogger _logger;
        private readonly MongoDBHandler _mongoDBHandler;

        public FrappeClient(IConfiguration configuration, ILogger logger, MongoDBHandler mongoDBHandler)
        {
            var frappeConfig = configuration.GetSection("Frappe");
            _baseUrl = frappeConfig["BaseUrl"];
            _apiEndpoint = frappeConfig["ApiEndpoint"];
            _apiKey = frappeConfig["ApiKey"];
            _apiSecret = frappeConfig["ApiSecret"];
            _logger = logger;
            _mongoDBHandler = mongoDBHandler;

            if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_apiEndpoint) ||
                string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
            {
                throw new ArgumentException("Frappe configuration is incomplete");
            }
        }

        public async Task<bool> SendReceiptAsync(Receipt receipt)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"token {_apiKey}:{_apiSecret}");

                try
                {
                    if (await DoesReceiptExistAsync(receipt.CheckNumber, receipt.BrandName))
                    {
                        _logger.Warning($"Receipt {receipt.CheckNumber} already exists in Frappe. Skipping upload.");
                        await _mongoDBHandler.MarkReceiptAsSent(new List<string> { receipt.Id });
                        return false;
                    }

                    if (receipt.PostingDate == null)
                    {
                        receipt.PostingDate = DateTime.Now;
                    }

                    var jsonContent = JsonSerializer.Serialize(receipt, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new CustomDateTimeConverter() }
                    });

                    _logger.Information($"Sending receipt {receipt.CheckNumber} to Frappe: {jsonContent}");

                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync($"{_baseUrl}{_apiEndpoint}", content);

                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        _logger.Error($"Failed to send receipt {receipt.CheckNumber} to Frappe: {response.ReasonPhrase}. Response: {responseBody}");
                        return false;
                    }

                    _logger.Information($"Successfully sent receipt {receipt.CheckNumber} to Frappe");

                    await _mongoDBHandler.MarkReceiptAsSent(new List<string> { receipt.Id });
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error sending receipt to Frappe: {ex.Message}");
                    return false;
                }
            }
        }

        private async Task<bool> DoesReceiptExistAsync(string checkNumber, string brandName)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"token {_apiKey}:{_apiSecret}");

                try
                {
                    var requestUrl = $"{_baseUrl}{_apiEndpoint}/query?filters=[['check_number','=', '{checkNumber}'], ['brand_name', '=', '{brandName}']]";
                    _logger.Information($"Checking if receipt {checkNumber} with brand {brandName} exists: {requestUrl}");

                    var response = await client.GetAsync(requestUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        _logger.Error($"Failed to check if receipt {checkNumber} exists: {response.ReasonPhrase}. Response: {responseBody}");
                        return false;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<Dictionary<string, object>>(content);

                    if (result.ContainsKey("data") && result["data"] is List<object> data && data.Count > 0)
                    {
                        _logger.Information($"Receipt {checkNumber} with brand {brandName} already exists in Frappe.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error checking if receipt exists: {ex.Message}");
                    return false;
                }

                _logger.Information($"Receipt {checkNumber} with brand {brandName} does not exist in Frappe.");
                return false;
            }
        }
    }

    public class CustomDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return DateTime.Parse(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    /// Model class for Receipt data
    public class Receipt
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [JsonPropertyName("id")]
        public string Id { get; set; } // Allow MongoDB to auto-generate _id

        [BsonRequired]
        [JsonPropertyName("checknumber")]
        public string CheckNumber { get; set; } // "000669"

        [BsonRequired]
        [JsonPropertyName("employeenumber")]
        public string EmployeeNumber { get; set; } // "000206"

        [JsonPropertyName("tablenumber")]
        public string TableNumber { get; set; } // "1/2"

        [JsonPropertyName("chknumber")]
        public string ChkNumber { get; set; } // Check number

        [JsonPropertyName("guestcount")]
        public string GuestCount { get; set; } // Guest count

        [BsonRequired]
        [JsonPropertyName("date")]
        public string Date { get; set; } // "02/15/2023"

        [JsonPropertyName("time")]
        public string Time { get; set; } // "10:30 am"

        [BsonRepresentation(BsonType.Decimal128)]
        [JsonPropertyName("subtotal")]
        public decimal Subtotal { get; set; } // 500.00

        [BsonRepresentation(BsonType.Decimal128)]
        [JsonPropertyName("vat")]
        public decimal VAT { get; set; } // 0

        [BsonRepresentation(BsonType.Decimal128)]
        [JsonPropertyName("tax")]
        public decimal Tax { get; set; } // 0

        [BsonRepresentation(BsonType.Int32)]
        public int IsSent { get; set; } = 0; // 0 = not sent, 1 = sent

        [BsonRepresentation(BsonType.Decimal128)]
        [JsonPropertyName("payment")]
        public decimal Payment { get; set; } // 600.00

        [JsonPropertyName("brand_name")]
        public string BrandName { get; set; }

        [BsonRepresentation(BsonType.DateTime)]
        [JsonPropertyName("posting_date")]
        public DateTime? PostingDate { get; set; } // Nullable to handle unsent receipts

        public override string ToString()
        {
            return $"Receipt {CheckNumber} - Date: {Date}, Payment: {Payment:C}";
        }
    }
}