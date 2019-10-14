using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Ical.Net;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using NodaTime;
using NodaTime.Serialization.JsonNet;
using NodaTime.Text;
using Microsoft.WindowsAzure.Storage.Blob;

[assembly: FunctionsStartup(typeof(FullCalendarIOtoiCalendarAF.Startup))]

namespace FullCalendarIOtoiCalendarAF
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddHttpClient();
            builder.Services.AddScoped<IClock, SystemClock>(c => SystemClock.Instance);
        }
    }

    public sealed class ScrapeCalendarToIcal
    {
        private const int YEARS_INTO_FUTURE = 3;
        private const int MONTHS_INTO_PAST = 6;

        private readonly HttpClient _httpClient;
        private readonly IClock _clock;
        public ScrapeCalendarToIcal(HttpClient httpClient, IClock clock)
        {
            _httpClient = httpClient;
            _clock = clock;
        }

        [FunctionName("ScrapeCalendarToIcal")]
        public async Task Run([TimerTrigger("0 0 6 * * *"
#if DEBUG
            , RunOnStartup=true
#endif
            )]TimerInfo myTimer, ILogger log, ExecutionContext context)
        //[Blob("$web/output.ics", FileAccess.Write)] Stream icsOutputBlob) - https://github.com/Azure/azure-functions-host/issues/3804
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var localTz = DateTimeZoneProviders.Tzdb[config.GetValue<string>("LocalTimezone")];

            var today = _clock.GetCurrentInstant().InZone(localTz).Date;

            var pastDate = today.PlusMonths(0 - MONTHS_INTO_PAST);
            var start = localTz.AtStrictly(new LocalDateTime(pastDate.Year, pastDate.Month, 1, 0, 0, 0)).ToInstant();
            var end = localTz.AtStrictly(new LocalDateTime(today.Year + YEARS_INTO_FUTURE, 12, 31, 23, 59, 59)).ToInstant();

            var requestUri = new Uri(config.GetValue<string>("SourceUrl"));

            var sourceEvents = await GetSourceEvents(requestUri, start, end);

            RecalculateAllDayTimes(sourceEvents);

            var calendar = MakeCalendar(config, sourceEvents, localTz, requestUri.Host);

            var serializer = new CalendarSerializer();
            var serializedCalendar = serializer.SerializeToString(calendar);

            var blobReference = await GetCloudBlockBlob(config);

            await blobReference.UploadTextAsync(serializedCalendar);
        }

        private async Task<IList<SourceEvent>> GetSourceEvents(Uri requestUri, Instant start, Instant end)
        {
            var formContent = new FormUrlEncodedContent(new[]
{
                new KeyValuePair<string, string>("start", start.ToUnixTimeSeconds().ToString()),
                new KeyValuePair<string, string>("end",  end.ToUnixTimeSeconds().ToString())
            });

            var response = await _httpClient.PostAsync(requestUri, formContent);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            var settings = new JsonSerializerSettings();
            settings.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
            settings.Converters.Remove(NodaConverters.LocalDateTimeConverter);
            settings.Converters.Add(new NodaPatternConverter<LocalDateTime>(LocalDateTimePattern.CreateWithInvariantCulture("yyyy'-'MM'-'dd' 'HH':'mm':'ss")));

            return JsonConvert.DeserializeObject<List<SourceEvent>>(responseBody, settings);
        }

        private static void RecalculateAllDayTimes(IList<SourceEvent> sourceEvents)
        {
            foreach (var sourceEvent in sourceEvents.Where(e => e.IsAllDay))
            {
                sourceEvent.Start = sourceEvent.Start.Date.AtMidnight();
                sourceEvent.End = sourceEvent.End.HasValue ? sourceEvent.End.Value.Date.PlusDays(1).AtMidnight() : sourceEvent.Start.Date.PlusDays(1).AtMidnight();
            }
        }

        private static Calendar MakeCalendar(IConfigurationRoot config, IList<SourceEvent> sourceEvents, DateTimeZone localTz, string idConstant)
        {
            var calendar = new Calendar();
            calendar.Properties.Add(new CalendarProperty { Name = "X-WR-CALNAME", Value = config.GetValue<string>("OutputCalendarName") });
            calendar.Properties.Add(new CalendarProperty { Name = "X-PUBLISHED-TTL", Value = "P1D" });
            calendar.Properties.Add(new CalendarProperty { Name = "REFRESH-INTERVAL", Value = "P1D" });

            calendar.Events.AddRange(sourceEvents
                .Select(e => new Ical.Net.CalendarComponents.CalendarEvent
                {
                    Uid = $"{e.Id}@{idConstant}",
                    Start = new CalDateTime(LocalDateTimeToDateTime(e.Start, localTz, e.IsAllDay)),
                    End = e.End.HasValue ? new CalDateTime(LocalDateTimeToDateTime(e.End.Value, localTz, e.IsAllDay)) : new CalDateTime(LocalDateTimeToDateTime(e.Start, localTz, e.IsAllDay)),
                    Summary = e.Title,
                    Description = e.Details,
                    Location = e.Location,
                    IsAllDay = e.IsAllDay
                }
                ));
            return calendar;
        }

        private static async Task<CloudBlockBlob> GetCloudBlockBlob(IConfigurationRoot config)
        {
            var connectionString = config.GetConnectionString("AzureStorageWebOutputConnectionString");
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(connectionString);
            var client = storageAccount.CreateCloudBlobClient();

            var container = client.GetContainerReference(config.GetValue<string>("OutputContainerReference"));
            await container.CreateIfNotExistsAsync();
            return container.GetBlockBlobReference(config.GetValue<string>("OutputFilePath"));
        }

        private static DateTime LocalDateTimeToDateTime(LocalDateTime ldt, DateTimeZone tz, bool justDate) => justDate ? ldt.Date.ToDateTimeUnspecified() : tz.AtLeniently(ldt).ToDateTimeUtc();
    }
}
