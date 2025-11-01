using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ==============================
//  GTFS Realtime Model Classes
// ==============================
public class RealtimeFeed
{
    public List<RealtimeEntity> entity { get; set; } = new();
}

public class RealtimeEntity
{
    public string id { get; set; }
    public TripUpdate trip_update { get; set; }
}

public class TripUpdate
{
    public TripDescriptor trip { get; set; }
    public List<StopTimeUpdate> stop_time_update { get; set; }
}

public class TripDescriptor
{
    public string trip_id { get; set; }
    public string route_id { get; set; }
}

public class StopTimeUpdate
{
    public int? stop_sequence { get; set; }
    public StopTimeEvent arrival { get; set; }
    public StopTimeEvent departure { get; set; }
    public string stop_id { get; set; }
}

public class StopTimeEvent
{
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? time { get; set; }
    public int? delay { get; set; }
}

// Converter to handle both numeric and string UNIX timestamps
public class FlexibleLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var val))
            return val;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (long.TryParse(str, out var val2))
                return val2;
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

// ==============================
//  SQLite + Output Data Models
// ==============================
public class BusSchedule
{
    public string RouteName { get; set; }
    public string RouteShortName { get; set; }
    public string TripHeadsign { get; set; }
    public string ServiceId { get; set; }
    public string ArrivalTime { get; set; }
    public string TripId { get; set; }
}

public class BusResult
{
    public string Route { get; set; }
    public string RouteShortName { get; set; }
    public string Destination { get; set; }
    public string ServiceId { get; set; }
    public string Scheduled { get; set; }
    public int? DelayMin { get; set; }
    public string FinalDue { get; set; }
    public string DueIn { get; set; }
}
