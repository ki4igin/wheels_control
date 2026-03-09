public static class Rtd100
{
    const double Alpha = 0.00385;
    const double R0 = 100;
    const double Gain = 16;
    const double Rref = 1.62e3;
    const double AdcScale = (1 << 22) * Gain;

    public static double AdcDataToTemperature(int adcData)
    {
        double R = adcData / AdcScale * Rref;
        return (R - R0) / (Alpha * R0);
    }
}
