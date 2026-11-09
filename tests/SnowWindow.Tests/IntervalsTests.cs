using SnowWindow.Simulation;

namespace SnowWindow.Tests;

public class IntervalsTests
{
    [Fact]
    public void Cutting_the_middle_splits_into_two_in_order()
    {
        var list = new List<(float, float)> { (0, 100) };
        Intervals.Subtract(list, 40, 60);
        Assert.Equal(new List<(float, float)> { (0, 40), (60, 100) }, list);
    }

    [Fact]
    public void Cutting_overlapping_edges_trims_each_interval()
    {
        var list = new List<(float, float)> { (0, 40), (60, 100) };
        Intervals.Subtract(list, 30, 70);
        Assert.Equal(new List<(float, float)> { (0, 30), (70, 100) }, list);
    }

    [Fact]
    public void Cutting_everything_leaves_nothing()
    {
        var list = new List<(float, float)> { (10, 20), (30, 40) };
        Intervals.Subtract(list, 0, 100);
        Assert.Empty(list);
    }

    [Fact]
    public void Disjoint_cut_changes_nothing()
    {
        var list = new List<(float, float)> { (10, 20) };
        Intervals.Subtract(list, 20, 30);
        Assert.Equal(new List<(float, float)> { (10, 20) }, list);
    }
}
