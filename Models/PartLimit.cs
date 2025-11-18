namespace CFACalculateWebAPI.Models
{
    public class PartLimit
    {
        public string Part { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TaskReference { get; set; } = string.Empty;
        public double? LowerLimit { get; set; }
        public double? UpperLimit { get; set; }
    }
}
