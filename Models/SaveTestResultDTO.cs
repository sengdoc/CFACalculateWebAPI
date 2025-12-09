using CFACalculateWebAPI.Models;

public class SaveTestResultDTO
{
    public List<TestResult>? vAutoResults { get; set; }
    public List<TestResult>? vVisualResults { get; set; }
    public string? PartProduct { get; set; }
    public string? CA { get; set; }        // new
    public string? SerialNo { get; set; }  // new
    public string? Task { get; set; }
    public string? TypeInput { get; set; }  // new
}

