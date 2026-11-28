using M2DPlus.Core.Experimental.RasterVisibility;

internal static class RasterControls
{
    internal static void Run()
    {
        RasterBuffer.Triangle[] triangles = [new(new(0,0,2),new(10,0,2),new(10,10,2),7), new(new(0,0,2),new(10,10,2),new(0,10,2),7)];
        foreach(var threads in new[]{1,4})
        {
            var grid=new RasterBuffer(0,0,10,10,128,triangles,threads);
            if(grid.Visible(new(5,5,1),.001) || !grid.Visible(new(5,5,3),.001) || !grid.Visible(new(-1,5,1),.001))
                throw new InvalidOperationException("Raster depth/coverage/bounds control failed");
        }
        Console.WriteLine("Raster controls passed (serial and parallel).");
    }
}
