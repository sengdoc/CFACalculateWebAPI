public class TimedFillResult
{
    public double[] ActTimedFillsIn { get; set; } = new double[10];
    public double[] RetTimedFills { get; set; } = new double[5];
    public double[] RetFinalFills { get; set; } = new double[5];
    public int[] gRunNo { get; set; } = new int[10];
    public int[] gStartNoMain { get; set; } = new int[10];
    public int[] gEndNoMain { get; set; } = new int[10];
    public int MainFillTimes { get; set; }
}
