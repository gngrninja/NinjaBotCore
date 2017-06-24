namespace NinjaBotCore.Models.DarkSky
{
    public class ForecastResponse
    {
        public float latitude { get; set; }
        public float longitude { get; set; }
        public string timezone { get; set; }
        public int offset { get; set; }
        public Currently currently { get; set; }
        public Minutely minutely { get; set; }
        public Hourly hourly { get; set; }
        public Daily daily { get; set; }
        public Alert[] alerts { get; set; }
        public Flags flags { get; set; }
    }

    public class Currently
    {
        public int time { get; set; }
        public string summary { get; set; }
        public string icon { get; set; }
        public float nearestStormDistance { get; set; }
        public float precipIntensity { get; set; }
        public float precipProbability { get; set; }
        public float temperature { get; set; }
        public float apparentTemperature { get; set; }
        public float dewPoint { get; set; }
        public float humidity { get; set; }
        public float windSpeed { get; set; }
        public float windBearing { get; set; }
        public float visibility { get; set; }
        public float cloudCover { get; set; }
        public float pressure { get; set; }
        public float ozone { get; set; }
    }

    public class Minutely
    {
        public string summary { get; set; }
        public string icon { get; set; }
        public Datum0[] data { get; set; }
    }

    public class Datum0
    {
        public int time { get; set; }
        public float precipIntensity { get; set; }
        public float precipProbability { get; set; }
        public float precipIntensityError { get; set; }
        public string precipType { get; set; }
    }

    public class Hourly
    {
        public string summary { get; set; }
        public string icon { get; set; }
        public Datum1[] data { get; set; }
    }

    public class Datum1
    {
        public int time { get; set; }
        public string summary { get; set; }
        public string icon { get; set; }
        public float precipIntensity { get; set; }
        public float precipProbability { get; set; }
        public string precipType { get; set; }
        public float temperature { get; set; }
        public float apparentTemperature { get; set; }
        public float dewPoint { get; set; }
        public float humidity { get; set; }
        public float windSpeed { get; set; }
        public int windBearing { get; set; }
        public float visibility { get; set; }
        public float cloudCover { get; set; }
        public float pressure { get; set; }
        public float ozone { get; set; }
        public float precipAccumulation { get; set; }
    }

    public class Daily
    {
        public string summary { get; set; }
        public string icon { get; set; }
        public Datum2[] data { get; set; }
    }

    public class Datum2
    {
        public int time { get; set; }
        public string summary { get; set; }
        public string icon { get; set; }
        public int sunriseTime { get; set; }
        public int sunsetTime { get; set; }
        public float moonPhase { get; set; }
        public float precipIntensity { get; set; }
        public float precipIntensityMax { get; set; }
        public int precipIntensityMaxTime { get; set; }
        public float precipProbability { get; set; }
        public string precipType { get; set; }
        public float temperatureMin { get; set; }
        public int temperatureMinTime { get; set; }
        public float temperatureMax { get; set; }
        public int temperatureMaxTime { get; set; }
        public float apparentTemperatureMin { get; set; }
        public int apparentTemperatureMinTime { get; set; }
        public float apparentTemperatureMax { get; set; }
        public int apparentTemperatureMaxTime { get; set; }
        public float dewPoint { get; set; }
        public float humidity { get; set; }
        public float windSpeed { get; set; }
        public int windBearing { get; set; }
        public float visibility { get; set; }
        public float cloudCover { get; set; }
        public float pressure { get; set; }
        public float ozone { get; set; }
        public float precipAccumulation { get; set; }
    }

    public class Flags
    {
        public string[] sources { get; set; }
        public string[] lampstations { get; set; }
        public string[] isdstations { get; set; }
        public string[] madisstations { get; set; }
        public string units { get; set; }
    }

    public class Alert
    {
        public string title { get; set; }
        public string[] regions { get; set; }
        public string severity { get; set; }
        public int time { get; set; }
        public int expires { get; set; }
        public string description { get; set; }
        public string uri { get; set; }
    }
}