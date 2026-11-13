namespace DeadGround.Engine;

internal readonly struct VegetationInterval
{
    public float StartT { get; }
    public float EndT { get; }
    public float Density { get; }
    public float CanopyHeight { get; }

    public VegetationInterval(float startT, float endT, float density, float canopyHeight)
    {
        StartT = startT;
        EndT = endT;
        Density = density;
        CanopyHeight = canopyHeight;
    }
}
