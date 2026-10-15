using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MyFirstFunctionApp;

// DTO for reading the Nager.Date response
public class NagerHolidayDto
{
    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    [JsonPropertyName("localName")]
    public string LocalName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

// DTO for our own HolidayController (also the queue message format)
public class HolidayDto
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
}

public class UpdateHolidayCalendar
{
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;

    public UpdateHolidayCalendar(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _logger = loggerFactory.CreateLogger<UpdateHolidayCalendar>();
        _httpClientFactory = httpClientFactory;

        // Read from settings (local.settings.json locally, Environment variables on Azure)
        _apiBaseUrl = configuration["TransportationApiBaseUrl"]
            ?? throw new InvalidOperationException("TransportationApiBaseUrl is not set");
    }

    [Function("UpdateHolidayCalendar")]
    [QueueOutput("%HolidaysQueueName%", Connection = "AzureWebJobsStorage")]
    public async Task<string[]> Run([TimerTrigger("0 0 3 1 * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("UpdateHolidayCalendar started at: {time}", DateTime.Now);

        var httpClient = _httpClientFactory.CreateClient();

        // 1. Get holidays for this year and next year
        var nagerHolidays = new List<NagerHolidayDto>();
        var thisYear = DateTime.Now.Year;

        foreach (var year in new[] { thisYear, thisYear + 1 })
        {
            var url = $"https://date.nager.at/api/v3/PublicHolidays/{year}/SE";
            var list = await httpClient.GetFromJsonAsync<List<NagerHolidayDto>>(url);
            if (list != null)
                nagerHolidays.AddRange(list);
        }

        if (!nagerHolidays.Any())
        {
            _logger.LogWarning("No holidays returned from Nager.Date");
            return Array.Empty<string>();
        }

        // 2. Get existing holidays from our own API
        var existingHolidays = await httpClient.GetFromJsonAsync<List<HolidayDto>>(
            $"{_apiBaseUrl}/api/Holiday") ?? new List<HolidayDto>();

        var existingDates = existingHolidays.Select(h => h.Date.Date).ToHashSet();

        // 3. New holidays -> one queue message each
        var messages = nagerHolidays
            .Where(h => !existingDates.Contains(h.Date.Date))
            .DistinctBy(h => h.Date.Date)
            .Select(h => JsonSerializer.Serialize(new HolidayDto { Date = h.Date }))
            .ToArray();

        _logger.LogInformation("UpdateHolidayCalendar finished. {count} messages sent to queue.", messages.Length);

        // The return value goes to the queue (Output Binding)
        return messages;
    }
}