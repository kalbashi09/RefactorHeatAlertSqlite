using RefactorHeatAlertPostGre.Data;
using RefactorHeatAlertPostGre.Data.Repositories;
using RefactorHeatAlertPostGre.Models.Entities;
using RefactorHeatAlertPostGre.Models.Enums;
using RefactorHeatAlertPostGre.Services.Interfaces;

namespace RefactorHeatAlertPostGre.Services
{
    public class AlertService : IAlertService
    {
        private readonly IHeatLogRepository _heatLogRepository;
        private readonly ISensorRepository _sensorRepository;
        private readonly INotificationService _notificationService;
        private readonly ILogger<AlertService> _logger;
        private readonly ISimulationService _simulationService;

        public AlertService(
            IHeatLogRepository heatLogRepository,
            ISensorRepository sensorRepository,
            INotificationService notificationService,
            ISimulationService simulationService,
            ILogger<AlertService> logger)
        {
            _heatLogRepository = heatLogRepository;
            _sensorRepository = sensorRepository;
            _notificationService = notificationService;
            _simulationService = simulationService;
            _logger = logger;
        }

        // I built this method to calculate the official NOAA Heat Index using the Rothfusz regression equation.
        // I take temperature in Celsius and humidity as inputs, convert to Fahrenheit for the formula, then convert back.
        // I built this method to calculate the official NOAA Heat Index using the Rothfusz regression equation.
        // I added a safety clamp to prevent physically impossible temperatures from corrupting the data.
        // I built this method to calculate the official NOAA Heat Index using the Rothfusz regression equation.
        // I added a double-layer safety clamp to prevent polynomial divergence and garbage data.
        private int CalculateHeatIndex(double tempCelsius, double humidity)
        {
            // LAYER 1: INPUT CLAMP
            // The NOAA polynomial mathematically explodes (produces garbage) above ~45°C (113°F).
            // We cap the input at 45°C to keep the formula in its valid, accurate range.
            if (tempCelsius > 45.0) tempCelsius = 45.0;
            if (tempCelsius < 10.0) tempCelsius = 10.0;

            double T = (tempCelsius * 9.0 / 5.0) + 32.0;
            double RH = humidity;
            
            if (T < 80.0)
            {
                return (int)Math.Round(tempCelsius);
                }
            
            double HI = -42.379 + 
                        (2.04901523 * T) + 
                        (10.14333127 * RH) - 
                        (0.22475541 * T * RH) - 
                        (0.00683783 * T * T) - 
                        (0.05481717 * RH * RH) + 
                        (0.00122874 * T * T * RH) + 
                        (0.00085282 * T * RH * RH) - 
                        (0.00000199 * T * T * RH * RH);
            
            if (RH < 13 && T >= 80 && T <= 112)
            {
                double adjustment = ((13 - RH) / 4.0) * Math.Sqrt((17 - Math.Abs(T - 95.0)) / 17.0);
                HI -= adjustment;
            }
            else if (RH > 85 && T >= 80 && T <= 87)
            {
                double adjustment = ((RH - 85) / 10.0) * ((87 - T) / 5.0);
                HI += adjustment;
            }
            
            double heatIndexCelsius = (HI - 32.0) * 5.0 / 9.0;
            
            // LAYER 2: OUTPUT CLAMP
            // Even with clamped input, we ensure the final Heat Index never exceeds a physically 
            // realistic maximum. (70°C is already apocalyptic and beyond human survival).
            if (heatIndexCelsius > 70.0) heatIndexCelsius = 70.0;
            
            return (int)Math.Round(heatIndexCelsius);
        }

        public async Task<AlertResult> ProcessHeatReadingAsync(Sensor sensor, int temperature, int humidity, CancellationToken cancellationToken = default)
        {
            // I calculate the actual heat index using both temperature and humidity
            int heatIndex = CalculateHeatIndex(temperature, humidity);
            
            // I pass the calculated heatIndex to CreateAlertResult
            var result = _simulationService.CreateAlertResult(sensor, heatIndex, humidity);
            
            // Always save to database - I store raw temperature in RecordedTemp and calculated heat index in HeatIndex
            await SaveHeatLogAsync(result, sensor.Id, temperature, cancellationToken);
            
            // Only broadcast if it meets alert threshold
            // if (ShouldSendAlert(heatIndex))
            // {
            //     var message = FormatAlertMessage(result);
            //     if (sensor.IsExternal)
            //     {
            //         message += "\n🌐 Source: Wokwi Virtual Device";
            //     }
            //     await _notificationService.BroadcastAlertAsync(message, cancellationToken);
            //     _logger.LogInformation("Alert broadcasted: {SensorCode} at {HeatIndex}°C", 
            //         sensor.SensorCode, heatIndex);
            // }
            
            return result;
        }

        public async Task BroadcastHeartbeatSummaryAsync(List<AlertResult> readings, CancellationToken cancellationToken = default)
        {
            var alarmingSpots = readings
                .Where(r => ShouldSendAlert(r.HeatIndex))
                .OrderByDescending(r => r.HeatIndex)
                .ToList();

            if (!alarmingSpots.Any())
            {
                _logger.LogDebug("No alarming spots in this cycle");
                return;
            }

            var message = FormatHeartbeatMessage(alarmingSpots);

            // ✅ Add the web app URL for the radar button
            string webAppUrl = "https://heatsync-zs03.onrender.com/mapUI.html";

            // ✅ Use the keyboard broadcast method
            await _notificationService.BroadcastAlertWithKeyboardAsync(message, webAppUrl, cancellationToken);

            _logger.LogInformation("Heartbeat broadcasted with {Count} alarming locations", alarmingSpots.Count);
        }

        public async Task SaveHeatLogAsync(AlertResult result, int sensorId, int actualTemperature, CancellationToken cancellationToken = default)
        {
            var heatLog = new HeatLog
            {
                SensorId = sensorId,
                RecordedTemp = actualTemperature,  // I store the actual temperature here
                HeatIndex = result.HeatIndex,       // I store the calculated heat index here
                Humidity = result.Humidity,
                RecordedAt = DateTime.UtcNow
            };
            
            await _heatLogRepository.CreateAsync(heatLog, cancellationToken);
            
            // Periodic cleanup - keep only latest 300 logs
            if (await _heatLogRepository.GetCountAsync(cancellationToken) > 350)
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);
                var deleted = await _heatLogRepository.DeleteOlderThanAsync(cutoff, cancellationToken);
                _logger.LogDebug("Cleaned up {Count} old heat logs", deleted);
            }
        }

        public bool ShouldSendAlert(int heatIndex)
        {
            return heatIndex >= 31; // Caution level and above
        }

        public string FormatAlertMessage(AlertResult result)
        {
            var level = _simulationService.GetDangerLevel(result.HeatIndex);
            var emoji = level.GetEmoji();

            return $"{emoji} *HEAT ALERT: {level.GetDisplayName()}*\n\n" +
                   $"📍 Location: {result.RelativeLocation} ({result.BarangayName})\n" +
                   $"🆔 Sensor: {result.SensorCode}\n" +
                   $"🔥 Heat Index: {result.HeatIndex}°C\n" +
                   $"💧 Humidity: {result.Humidity}%\n" +
                   $"⏰ Time: {result.CreatedAt:hh:mm tt}";
        }

        private string FormatHeartbeatMessage(List<AlertResult> alarmingSpots)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🌡️ ***HEATSYNC: HIGH HEAT REPORT***");
            sb.AppendLine($"⏰ *Scanned at: {DateTime.Now:hh:mm tt}*");
            sb.AppendLine("-----------------------------------");

            var topSpot = alarmingSpots.First();
            sb.AppendLine($"🔝 **HIGHEST:** {topSpot.HeatIndex}°C in {topSpot.BarangayName}");
            sb.AppendLine();

            foreach (var spot in alarmingSpots)
            {
                var level = _simulationService.GetDangerLevel(spot.HeatIndex);
                var emoji = level.GetEmoji();
                
                sb.AppendLine($"{emoji} *{spot.HeatIndex}°C* - {level.GetDisplayName()}");
                sb.AppendLine($"📍 {spot.DisplayName} ({spot.BarangayName})");
                sb.AppendLine($"💧 Humidity: {spot.Humidity}%");
                if (spot.DisplayName.Contains("(Wokwi)"))
                {
                    sb.AppendLine("🌐 Source: Wokwi Virtual Device");
                }
                sb.AppendLine();
            }
            sb.AppendLine(" ✅ *Stay Hydrated, Avoid going out during peak heat hours.*");
            sb.AppendLine("📍 *Tap the button below for the live interactive radar.*");
            
            return sb.ToString();
        }
    }
}