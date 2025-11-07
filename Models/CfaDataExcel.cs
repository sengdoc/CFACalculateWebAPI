using System;

namespace CFACalculateWebAPI.Models
{
    // This model represents the cfa_data_excel table
    public class CfaDataExcel
    {
        public int AuditID { get; set; }
        public int SampleID { get; set; }
        public DateTime SampleTime { get; set; }
        public float Seconds { get; set; }
        public string Voltage { get; set; } = "";
        public string Current { get; set; } = "";
        public string Power { get; set; } = "";
        public string PowerUsage { get; set; } = "";
        public string WaterUsage { get; set; } = "";
        public string Temperature { get; set; } = "";
        public string WaterPressure { get; set; } = "";
        public string WaterTemperature { get; set; } = "";
    }
}
