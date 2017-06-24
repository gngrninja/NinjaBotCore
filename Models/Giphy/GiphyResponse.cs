namespace NinjaBotCore.Models.Giphy
{
    public class GiphyReponse
    {
        public Data data { get; set; }
        public Meta meta { get; set; }
    }

    public class Data
    {
        public string type { get; set; }
        public string id { get; set; }
        public string url { get; set; }
        public string image_original_url { get; set; }
        public string image_url { get; set; }
        public string image_mp4_url { get; set; }
        public string image_frames { get; set; }
        public string image_width { get; set; }
        public string image_height { get; set; }
        public string fixed_height_downsampled_url { get; set; }
        public string fixed_height_downsampled_width { get; set; }
        public string fixed_height_downsampled_height { get; set; }
        public string fixed_width_downsampled_url { get; set; }
        public string fixed_width_downsampled_width { get; set; }
        public string fixed_width_downsampled_height { get; set; }
        public string fixed_height_small_url { get; set; }
        public string fixed_height_small_still_url { get; set; }
        public string fixed_height_small_width { get; set; }
        public string fixed_height_small_height { get; set; }
        public string fixed_width_small_url { get; set; }
        public string fixed_width_small_still_url { get; set; }
        public string fixed_width_small_width { get; set; }
        public string fixed_width_small_height { get; set; }
    }

    public class Meta
    {
        public int status { get; set; }
        public string msg { get; set; }
    }
}