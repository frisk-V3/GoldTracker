using System.Text.Json;

namespace GoldMonitorService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly HttpClient _httpClient = new HttpClient();
        
        // --- 設定 ---
        private const string ApiKey = "YOUR_API_KEY";
        private const string RawCsv = "gold_raw_history.csv"; // 全記録
        private const string StudyJson = "study_result.json";  // 3日間の学習成果
        private const double IntervalMinutes = 14.5;
        
        private readonly DateTime _startTime;
        private readonly DateTime _studyEndTime;
        
        private List<double> _studyPrices = new(); // 3日間の価格を貯める

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _startTime = DateTime.Now;
            _studyEndTime = _startTime.AddDays(3); // 3日間の集中計測

            if (!File.Exists(RawCsv))
                File.WriteAllText(RawCsv, "Timestamp,Price,Ask,Bid\n");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"【監視開始】{_startTime} から計測をスタート。");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. APIから取得
                    var (price, ask, bid) = await GetGoldPriceAsync();

                    // 2. CSVへは「常に」記録し続ける（証拠を残す）
                    string line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss},{price},{ask},{bid}";
                    await File.AppendAllTextAsync(RawCsv, line + "\n", stoppingToken);

                    // 3. 最初の3日間だけ学習データを蓄積
                    if (DateTime.Now <= _studyEndTime)
                    {
                        _studyPrices.Add(price);
                        _logger.LogInformation($"[学習中] 3日間完了まであと {(int)(_studyEndTime - DateTime.Now).TotalHours}時間");
                    }
                    else if (_studyPrices.Count > 0)
                    {
                        // 3日間経過した瞬間に一度だけJSONで成果を出力
                        await SaveStudyResultAsync();
                        _studyPrices.Clear(); // 二度書きしないようにクリア
                    }

                    _logger.LogInformation($"記録完了: {price}円");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"通信エラー: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), stoppingToken);
            }
        }

        private async Task SaveStudyResultAsync()
        {
            var result = new
            {
                Title = "3日間集中計測レポート",
                Period = $"{_startTime} ～ {_studyEndTime}",
                MaxPrice = _studyPrices.Max(),
                MinPrice = _studyPrices.Min(),
                AveragePrice = _studyPrices.Average(),
                Fluctuation = _studyPrices.Last() - _studyPrices.First(),
                SampleCount = _studyPrices.Count,
                Verdict = "計測完了。このデータを元に、待つか買うか判断してください。"
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(result, options);
            await File.WriteAllTextAsync(StudyJson, json);
            _logger.LogInformation("【学習完了】成果を JSON に書き出しました。");
        }

        private async Task<(double price, double ask, double bid)> GetGoldPriceAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://goldapi.io");
            request.Headers.Add("x-access-token", ApiKey);
            var res = await _httpClient.SendAsync(request);
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return (r.GetProperty("price").GetDouble(), r.GetProperty("ask").GetDouble(), r.GetProperty("bid").GetDouble());
        }
    }
}
