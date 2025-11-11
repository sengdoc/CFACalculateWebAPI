public class FinalRinseA
{
    public double[] Values { get; set; }

    public FinalRinseA(int size)
    {
        Values = new double[size];
    }

    public double this[int index]
    {
        get => Values[index];
        set => Values[index] = value;
    }
}