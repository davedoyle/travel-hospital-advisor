using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ApiTests
{
    class Program
    {
        // A single shared HttpClient instance for all calls
        private static readonly HttpClient client = new HttpClient();

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Travel to Hospital Advisor API Test ===\n");

            await TestTfiApi();
            Console.WriteLine();
            await TestWeatherApi();
        }

        // Test 1: TFI GTFS-Realtime API
     private static async Task TestTfiApi()
        {
            Console.WriteLine("Checking TFI GTFS-Realtime feed (JSON mode)...\n");

            string apiUrl = "https://api.nationaltransport.ie/gtfsr/v2/gtfsr?format=json";
            //string apiUrl = "https://api.nationaltransport.ie/gtfsr/v2/gtfsr";  // no ?format


            try
            {
                // wipe headers first, then add key again
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("x-api-key", "5f37f29af0364c70a364b3e034deb877");

                // call the API
                var response = await client.GetStringAsync(apiUrl);

                // parse to JSON
                JObject json = JObject.Parse(response);

                // just show a few top-level details for now
                Console.WriteLine("Feed header info:");
                Console.WriteLine($"Timestamp: {json["header"]?["timestamp"]}");
                Console.WriteLine($"Entity count: {json["entity"]?.Count() ?? 0}");

                // show first bus trip as a sample
                var firstEntity = json["entity"]?.First;
                if (firstEntity != null)
                {
                    Console.WriteLine("\nSample Trip:");
                    Console.WriteLine(firstEntity.ToString().Substring(0, Math.Min(300, firstEntity.ToString().Length)));
                }
                else
                {
                    Console.WriteLine("No active trip data found.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TFI API call failed: {ex.Message}");
            }
        }



        // Test 2: Open-Meteo API
        private static async Task TestWeatherApi()
        {
            Console.WriteLine("Checking Open-Meteo Weather API...\n");
            double latitude = 51.89;   // Cork
            double longitude = -8.47;  // Cork
            string apiUrl = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current_weather=true";

            try
            {
                var response = await client.GetStringAsync(apiUrl);
                JObject json = JObject.Parse(response);

                var current = json["current_weather"];
                if (current != null)
                {
                    Console.WriteLine($"Temperature: {current["temperature"]}°C");
                    Console.WriteLine($"Windspeed: {current["windspeed"]} km/h");
                    Console.WriteLine($"Weather code: {current["weathercode"]}");
                }
                else
                {
                    Console.WriteLine("Weather data missing in response.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Weather API call failed: {ex.Message}");
            }
        }
    }
}
