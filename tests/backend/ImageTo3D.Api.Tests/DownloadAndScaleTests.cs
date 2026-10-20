using System.Net;
using System.Net.Http.Json;
using System.Text;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Tests;

public class DownloadAndScaleTests
{
    [Fact]
    public async Task DownloadGlb_ReturnsRealGlbInMetres()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.GetAsync($"/api/models/{job.Id}/download?format=glb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("model/gltf-binary", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".glb", response.Content.Headers.ContentDisposition!.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("glTF", Encoding.ASCII.GetString(bytes, 0, 4));
        var size = MeshOperations.ComputeBounds(GlbReader.Read(bytes)).Size;
        Assert.Equal(0.1, size.Y, 6); // 100 mm = 0.1 m
    }

    [Fact]
    public async Task DownloadStl_ReturnsBinaryStlInMillimetresZUp()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.GetAsync($"/api/models/{job.Id}/download?format=stl");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("model/stl", response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEqual("glTF", Encoding.ASCII.GetString(bytes, 0, 4)); // never a renamed GLB
        Assert.Contains("units=mm", Encoding.ASCII.GetString(bytes, 0, 80));

        var mesh = StlReader.ReadBinary(bytes);
        Assert.True(MeshValidator.Validate(mesh).IsValid);
        var bounds = MeshOperations.ComputeBounds(mesh);
        Assert.Equal(50, bounds.Size.X, 3);
        Assert.Equal(25, bounds.Size.Y, 3);  // depth
        Assert.Equal(100, bounds.Size.Z, 3); // height is Z in the STL
        Assert.Equal(0, bounds.Min.Z, 6);    // resting on the bed
        Assert.True(MeshOperations.SignedVolume(mesh) > 0);
    }

    [Fact]
    public async Task Preview_ServesGlbInline()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.GetAsync($"/api/models/{job.Id}/preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Download_BeforeSuccess_Returns409()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();
        var job = await client.CreateJobAsync();

        var response = await client.GetAsync($"/api/models/{job.Id}/download?format=glb");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_ready", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Download_UnknownJob_Returns404()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync($"/api/models/{Guid.NewGuid()}/download?format=stl");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("../../appsettings.json")]
    public async Task Download_InvalidFormat_Returns400(string format)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.GetAsync($"/api/models/{job.Id}/download?format={Uri.EscapeDataString(format)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_MissingArtifactFile_Returns404()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        File.Delete(Path.Combine(factory.Root, "storage", job.Id.ToString("N"), "model.stl"));

        var response = await client.GetAsync($"/api/models/{job.Id}/download?format=stl");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("artifact_missing", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Scale_SetsChosenDimensionAndRebuildsArtifacts()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.PutAsJsonAsync($"/api/models/{job.Id}/scale", new { axis = "X", targetMm = 30.0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scaled = await response.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);
        Assert.Equal(30, scaled!.DimensionsMm!.X, 3);
        Assert.Equal(60, scaled.DimensionsMm.Y, 3);
        Assert.Equal(15, scaled.DimensionsMm.Z, 3);

        var stl = StlReader.ReadBinary(await client.GetByteArrayAsync($"/api/models/{job.Id}/download?format=stl"));
        Assert.Equal(60, MeshOperations.ComputeBounds(stl).Size.Z, 3);

        // Repeated scaling is computed from the raw mesh, so it does not compound.
        await client.PutAsJsonAsync($"/api/models/{job.Id}/scale", new { axis = "Longest", targetMm = 100.0 });
        var again = await client.GetJobAsync(job.Id);
        Assert.Equal(100, again.DimensionsMm!.Y, 6);
    }

    [Theory]
    [InlineData("{\"axis\":\"Y\",\"targetMm\":-1}")]
    [InlineData("{\"axis\":\"Y\",\"targetMm\":0.01}")]
    [InlineData("{\"axis\":\"Y\",\"targetMm\":20000}")]
    [InlineData("{\"targetMm\":50}")]
    [InlineData("{\"axis\":\"Diagonal\",\"targetMm\":50}")]
    [InlineData("{\"axis\":\"Y\"}")]
    public async Task Scale_InvalidRequest_Returns400(string body)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        var response = await client.PutAsync($"/api/models/{job.Id}/scale", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scale_BeforeSuccess_Returns409()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();
        var job = await client.CreateJobAsync();

        var response = await client.PutAsJsonAsync($"/api/models/{job.Id}/scale", new { axis = "Y", targetMm = 50.0 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SourceImage_IsServedAsPng()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.CreateJobAsync();

        var response = await client.GetAsync($"/api/models/{job.Id}/image?kind=source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
    }
}
