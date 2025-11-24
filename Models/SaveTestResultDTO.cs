using CFACalculateWebAPI.Models;

public class SaveTestResultDTO
{
    public List<TestResult>? vAutoResults { get; set; }
    public List<TestResult>? vVisualResults { get; set; }
    public string? PartProduct { get; set; }
}
