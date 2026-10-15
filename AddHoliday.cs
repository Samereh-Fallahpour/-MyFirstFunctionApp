using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MyFirstFunctionApp;

public class AddHoliday
{
    private readonly ILogger<AddHoliday> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;

    private const string AddHolidayEndpoint = "/api/Holiday";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AddHoliday(ILogger<AddHoliday> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        // Read from settings (local.settings.json locally, Environment variables on Azure)
        _apiBaseUrl = configuration["TransportationApiBaseUrl"]
            ?? throw new InvalidOperationException("TransportationApiBaseUrl is not set");
    }

    [Function(nameof(AddHoliday))]
    public async Task Run(
        [QueueTrigger("%HolidaysQueueName%", Connection = "AzureWebJobsStorage")] QueueMessage message)
    {
        _logger.LogInformation("AddHoliday: message {MessageId}, attempt {DequeueCount}",
            message.MessageId, message.DequeueCount);

        // 1. Convert the message to a DTO
        HolidayDto? holiday;
        try
        {
            holiday = JsonSerializer.Deserialize<HolidayDto>(message.MessageText, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in message {MessageId}: {Text}",
                message.MessageId, message.MessageText);
            return; // Broken message: retrying will not help
        }

        if (holiday is null)
        {
            _logger.LogError("Message {MessageId} deserialized to null", message.MessageId);
            return;
        }

        // 2. Save through HolidayController
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_apiBaseUrl);

        var response = await client.PostAsJsonAsync(AddHolidayEndpoint, holiday);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Saving holiday failed: {Status} {Body}", (int)response.StatusCode, body);

            // Throw -> message stays in queue -> retry -> after 5 tries: poison queue
            throw new HttpRequestException($"HolidayController returned {(int)response.StatusCode}");
        }

        _logger.LogInformation("Holiday saved: {Holiday}", message.MessageText);
    }
}