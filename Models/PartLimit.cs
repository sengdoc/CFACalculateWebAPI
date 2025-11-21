namespace CFACalculateWebAPI.Models
{
    public class PartLimit
    {
        public string? Part { get; set; }
        public string? Class { get; set; }
        public string? Description { get; set; }
        public string? TaskReference { get; set; }

        public double? LowerLimit { get; set; }
        public double? UpperLimit { get; set; }
    }
}
