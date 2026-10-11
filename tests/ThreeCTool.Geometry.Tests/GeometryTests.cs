using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml;

using ThreeCTool.Core.Projects;

using Xunit;

namespace ThreeCTool.Geometry.Tests;

public sealed class MeshTests
{
    [Fact]
    public void Box_HasExpectedAreaVolumeAndBounds()
    {
        var box = TestMeshes.Box(Vector3.Zero, new Vector3(10, 20, 30));
        Assert.Equal(6000, box.SignedVolume(), 3);
        Assert.Equal(2 * ((10 * 20) + (20 * 30) + (10 * 30)), box.SurfaceArea(), 3);
        Assert.Equal(new Vector3(10, 20, 30), box.Bounds.Size);
        Assert.True(MeshRepair.IsWatertight(box));
    }

    [Fact]
    public void Transform_MirrorKeepsNormalsOutward()
    {
        var mirrored = TestMeshes.UnitCube().Transform(Matrix4x4.CreateScale(-1, 1, 1));
        Assert.Equal(1, mirrored.SignedVolume(), 5);
        Assert.Equal(-1, mirrored.Bounds.Min.X);
    }

    [Fact]
    public void VertexNormals_PointOutwardOnSphere()
    {
        var sphere = TestMeshes.Sphere(10, 16, 32);
        var normals = sphere.ComputeVertexNormals();
        for (var i = 0; i < sphere.VertexCount; i++)
        {
            Assert.True(Vector3.Dot(normals[i], Vector3.Normalize(sphere.Vertices[i])) > 0.99f);
        }
    }

    [Fact]
    public void Constructor_RejectsOutOfRangeIndices()
    {
        Assert.Throws<ArgumentException>(() => new Mesh([Vector3.Zero], [0, 0, 1]));
    }

    [Fact]
    public void BoundingBox_TransformCoversRotatedCorners()
    {
        var box = new BoundingBox(Vector3.Zero, new Vector3(2, 1, 1));
        var rotated = box.Transform(Matrix4x4.CreateRotationZ(MathF.PI / 2));
        Assert.Equal(-1, rotated.Min.X, 5);
        Assert.Equal(2, rotated.Max.Y, 5);
    }
}

public sealed class StlTests
{
    [Fact]
    public void Binary_RoundTripWeldsVertices()
    {
        var stream = new MemoryStream();
        StlWriter.Write(stream, TestMeshes.UnitCube());
        stream.Position = 0;

        var mesh = Assert.Single(StlReader.Read(stream, "cube"));
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(1, mesh.SignedVolume(), 5);
        Assert.Equal("cube", mesh.Name);
    }

    [Fact]
    public void Binary_WithSolidHeader_IsReadAsBinary()
    {
        var stream = new MemoryStream();
        StlWriter.Write(stream, TestMeshes.UnitCube());
        var bytes = stream.ToArray();
        Encoding.ASCII.GetBytes("solid exported by CAD").CopyTo(bytes, 0);

        var mesh = Assert.Single(StlReader.Read(new MemoryStream(bytes), "cube"));
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void Ascii_MultiSolid_ProducesNamedMeshes()
    {
        const string text = """
            solid core_mill
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 1 0
                endloop
              endfacet
            endsolid core_mill
            solid skin_print
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1e1 0 1
                  vertex 0 2.5 1
                endloop
              endfacet
            endsolid
            """;
        var meshes = StlReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(text)), "file");
        Assert.Equal(["core_mill", "skin_print"], meshes.Select(m => m.Name));
        Assert.Equal(10, meshes[1].Bounds.Max.X);
        Assert.Equal(BodyRole.Print, MeshImporter.InferRole(meshes[1].Name));
        Assert.Equal(BodyRole.Mill, MeshImporter.InferRole(meshes[0].Name));
    }

    [Theory]
    [InlineData("not an stl file at all")]
    [InlineData("solid x\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nendloop\nendfacet\nendsolid")]
    [InlineData("solid x\nfacet normal 0 0 1\nouter loop\nvertex 0 0 NaN\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid")]
    public void Invalid_Throws(string text)
    {
        Assert.Throws<InvalidDataException>(() => StlReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(text)), "x"));
    }

    [Fact]
    public void Binary_TruncatedOrOverclaimedCount_Throws()
    {
        var stream = new MemoryStream();
        StlWriter.Write(stream, TestMeshes.UnitCube());
        var bytes = stream.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 80);
        Assert.Throws<InvalidDataException>(() => StlReader.Read(new MemoryStream(bytes), "x"));
    }

    [Fact]
    public void Importer_LoadsStlFileWithHash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"3ctool-{Guid.NewGuid():N}.stl");
        try
        {
            StlWriter.Write(path, TestMeshes.UnitCube());
            var model = MeshImporter.Load(path);
            Assert.False(model.UnitsDeclared);
            Assert.Single(model.Bodies);
            Assert.Equal(64, model.Sha256.Length);
            Assert.Equal(Vector3.One, model.Bounds.Size);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ThreeMfTests
{
    private const string Rels = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel" />
        </Relationships>
        """;

    private const string CubeVertices = """
        <vertices>
          <vertex x="0" y="0" z="0" /><vertex x="1" y="0" z="0" /><vertex x="0" y="1" z="0" /><vertex x="1" y="1" z="0" />
          <vertex x="0" y="0" z="1" /><vertex x="1" y="0" z="1" /><vertex x="0" y="1" z="1" /><vertex x="1" y="1" z="1" />
        </vertices>
        <triangles>
          <triangle v1="0" v2="2" v3="3" /><triangle v1="0" v2="3" v3="1" /><triangle v1="4" v2="5" v3="7" /><triangle v1="4" v2="7" v3="6" />
          <triangle v1="0" v2="1" v3="5" /><triangle v1="0" v2="5" v3="4" /><triangle v1="2" v2="6" v3="7" /><triangle v1="2" v2="7" v3="3" />
          <triangle v1="0" v2="4" v3="6" /><triangle v1="0" v2="6" v3="2" /><triangle v1="1" v2="3" v3="7" /><triangle v1="1" v2="7" v3="5" />
        </triangles>
        """;

    [Fact]
    public void Components_AreFlattenedIntoNamedBodiesInMillimetres()
    {
        var model = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <model unit="centimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <resources>
                <object id="1" type="model" name="core mill"><mesh>{CubeVertices}</mesh></object>
                <object id="2" type="model" name="skin print"><mesh>{CubeVertices}</mesh></object>
                <object id="3" type="model" name="assembly">
                  <components>
                    <component objectid="1" />
                    <component objectid="2" transform="1 0 0 0 1 0 0 0 1 5 0 0" />
                  </components>
                </object>
              </resources>
              <build><item objectid="3" transform="1 0 0 0 1 0 0 0 1 0 0 2" /></build>
            </model>
            """;

        var bodies = ThreeMfReader.Read(CreatePackage(model));
        Assert.Equal(["core mill", "skin print"], bodies.Select(b => b.Name));
        Assert.Equal(new Vector3(0, 0, 20), bodies[0].Bounds.Min);
        Assert.Equal(new Vector3(60, 10, 30), bodies[1].Bounds.Max);
        Assert.Equal(1000, bodies[0].SignedVolume(), 2);
    }

    [Fact]
    public void Dtd_IsRejected()
    {
        const string model = """
            <?xml version="1.0"?>
            <!DOCTYPE model [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"><resources /><build /></model>
            """;
        Assert.Throws<XmlException>(() => ThreeMfReader.Read(CreatePackage(model)));
    }

    [Fact]
    public void ComponentCycle_Throws()
    {
        const string model = """
            <?xml version="1.0"?>
            <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <resources>
                <object id="1"><components><component objectid="2" /></components></object>
                <object id="2"><components><component objectid="1" /></components></object>
              </resources>
              <build><item objectid="1" /></build>
            </model>
            """;
        Assert.Throws<InvalidDataException>(() => ThreeMfReader.Read(CreatePackage(model)));
    }

    private static MemoryStream CreatePackage(string model)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "_rels/.rels", Rels);
            Write(archive, "3D/3dmodel.model", model);
        }

        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}

public sealed class WeldAndRepairTests
{
    [Fact]
    public void Weld_MergesNearbyVertices()
    {
        var a = TestMeshes.UnitCube();
        var b = TestMeshes.Box(new Vector3(1.00001f, 0, 0), new Vector3(2, 1, 1));
        var welded = MeshWelder.Weld(Mesh.Merge([a, b]), 0.001f);
        Assert.Equal(12, welded.VertexCount);
    }

    [Fact]
    public void Repair_FixesDuplicatesDegeneratesFlipsAndHoles()
    {
        var cube = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 10));
        var indices = cube.Indices.ToList();

        // Flip one triangle, duplicate one, add a degenerate one, then remove the +X face (4-edge hole)
        // and one -Z triangle (3-edge hole).
        (indices[13], indices[14]) = (indices[14], indices[13]);
        indices.AddRange(indices.Skip(3).Take(3));
        indices.AddRange([0, 0, 1]);
        indices.RemoveRange(30, 6);
        indices.RemoveRange(0, 3);

        var broken = new Mesh(cube.Vertices, [.. indices]);
        Assert.False(MeshRepair.IsWatertight(broken));

        var (fixedMesh, report) = MeshRepair.Repair(broken);
        Assert.Equal(1, report.RemovedDuplicateTriangles);
        Assert.Equal(1, report.RemovedDegenerateTriangles);
        Assert.Equal(1, report.FlippedTriangles);
        Assert.Equal(2, report.HolesFilled);
        Assert.Equal(0, report.NonManifoldEdges);
        Assert.True(report.IsWatertight);
        Assert.Equal(1000, fixedMesh.SignedVolume(), 2);
    }

    [Fact]
    public void Repair_TurnsInsideOutMeshOutward()
    {
        var inverted = TestMeshes.Sphere(5, 8, 16).Transform(Matrix4x4.Identity);
        var indices = (int[])inverted.Indices.Clone();
        for (var t = 0; t < indices.Length; t += 3)
        {
            (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        }

        var (repaired, report) = MeshRepair.Repair(new Mesh(inverted.Vertices, indices));
        Assert.Equal(inverted.TriangleCount, report.FlippedTriangles);
        Assert.True(repaired.SignedVolume() > 0);
        Assert.True(report.IsWatertight);
    }
}

public sealed class SpatialTests
{
    [Fact]
    public void Bvh_MatchesBruteForceRaycasts()
    {
        var sphere = TestMeshes.Sphere(20, 24, 48);
        var bvh = new Bvh(sphere);
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var origin = new Vector3(random.NextSingle() * 80 - 40, random.NextSingle() * 80 - 40, random.NextSingle() * 80 - 40);
            var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));

            var expected = BruteForce(sphere, origin, direction);
            var hit = bvh.Raycast(origin, direction, float.MaxValue, out var result);
            Assert.Equal(expected.HasValue, hit);
            if (hit)
            {
                Assert.Equal(expected!.Value, result.Distance, 3);
                Assert.Equal(hit, bvh.Occluded(origin, direction, float.MaxValue));
            }
        }
    }

    [Fact]
    public void Bvh_QueryReturnsTrianglesOverlappingBox()
    {
        var sphere = TestMeshes.Sphere(20, 24, 48);
        var bvh = new Bvh(sphere);
        var box = new BoundingBox(new Vector3(-5, -5, 15), new Vector3(5, 5, 25));
        var found = new HashSet<int>();
        bvh.Query(box, t => found.Add(t));

        for (var t = 0; t < sphere.TriangleCount; t++)
        {
            var (a, b, c) = sphere.GetTriangle(t);
            var bounds = new BoundingBox(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
            Assert.Equal(box.Intersects(bounds), found.Contains(t));
        }

        Assert.NotEmpty(found);
    }

    [Fact]
    public void XyGrid_ContainsEveryTriangleAtItsCentroid()
    {
        var sphere = TestMeshes.Sphere(20, 12, 24);
        var grid = new XyGrid(sphere, 3);
        for (var t = 0; t < sphere.TriangleCount; t++)
        {
            var (a, b, c) = sphere.GetTriangle(t);
            var centroid = (a + b + c) / 3;
            Assert.Contains(t, grid.TrianglesAt(centroid.X, centroid.Y).ToArray());
        }

        Assert.True(grid.TrianglesAt(100, 100).IsEmpty);
    }

    private static float? BruteForce(Mesh mesh, Vector3 origin, Vector3 direction)
    {
        float? best = null;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f)
            {
                continue;
            }

            var s = origin - a;
            var u = Vector3.Dot(s, p) / det;
            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(direction, q) / det;
            var d = Vector3.Dot(e2, q) / det;
            if (u >= 0 && v >= 0 && u + v <= 1 && d > 0 && (best is null || d < best))
            {
                best = d;
            }
        }

        return best;
    }
}

public sealed class TransformAndKinematicsTests
{
    [Fact]
    public void Compose_AppliesUnitThenStepsInOrder()
    {
        var steps = new[]
        {
            MeshTransforms.Rotate(Vector3.UnitZ, 90),
            MeshTransforms.Translate(new Vector3(10, 0, 0)),
        };
        var matrix = MeshTransforms.Compose(Core.LengthUnit.Inch, steps);
        var p = Vector3.Transform(Vector3.UnitX, matrix);
        Assert.Equal(10, p.X, 4);
        Assert.Equal(25.4f, p.Y, 4);
    }

    [Fact]
    public void Scale_RejectsNonPositive()
    {
        Assert.Throws<ArgumentException>(() => MeshTransforms.ToMatrix(MeshTransforms.Scale(0)));
        Assert.Throws<ArgumentException>(() => MeshTransforms.ToMatrix(MeshTransforms.Rotate(Vector3.Zero, 10)));
    }

    [Fact]
    public void RotaryKinematics_PositiveATurnsYTowardsZ()
    {
        var toMachine = RotaryKinematics.PartToMachine(90, 0, 200);
        var p = Vector3.Transform(new Vector3(5, 1, 200), toMachine);
        Assert.Equal(new Vector3(5, 0, 201), p, new VectorComparer(1e-4f));

        var back = Vector3.Transform(p, RotaryKinematics.MachineToPart(90, 0, 200));
        Assert.Equal(new Vector3(5, 1, 200), back, new VectorComparer(1e-4f));

        var (y, z) = RotaryKinematics.Rotate(1, 200, 90, 0, 200);
        Assert.Equal(0, y, 9);
        Assert.Equal(201, z, 9);
    }

    private sealed class VectorComparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }
}

public sealed class PlacementTests
{
    [Fact]
    public void OnBoxStock_CentresXyAndAlignsTop()
    {
        var part = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 5)).Bounds;
        var stock = new Stock { Type = StockType.Box, SizeX = 100, SizeY = 50, SizeZ = 20 };
        Assert.Equal(new Vector3(45, 20, 15), Placement.OnBoxStock(part, stock, StockAnchor.Top));
        Assert.Equal(0, Placement.OnBoxStock(part, stock, StockAnchor.Bottom).Z);
    }

    [Fact]
    public void EnclosingCircle_IsMinimal()
    {
        var square = TestMeshes.Box(new Vector3(0, -10, -10), new Vector3(1, 10, 10));
        var (y, z, r) = Placement.EnclosingCircleYz(square);
        Assert.Equal(0, y, 5);
        Assert.Equal(0, z, 5);
        Assert.Equal(Math.Sqrt(200), r, 4);
    }

    [Fact]
    public void OnRotaryAxis_FitsPartIntoCylinderStock()
    {
        var part = TestMeshes.Sphere(20, 12, 24).Transform(Matrix4x4.CreateTranslation(300, 40, 60));
        var stock = new Stock { Type = StockType.Cylinder, OriginX = 0, OriginY = 0, OriginZ = 180, Diameter = 42, Length = 100 };
        Assert.False(Placement.FitsInCylinder(part, stock));

        var placed = part.Transform(Matrix4x4.CreateTranslation(Placement.OnRotaryAxis(part, stock)));
        Assert.True(Placement.FitsInCylinder(placed, stock));
        Assert.Equal(50, placed.Bounds.Center.X, 3);
    }

    [Fact]
    public void Orientation_ThreeAxis_PrefersNoUndercut()
    {
        var candidates = OrientationAdvisor.Suggest(TestMeshes.Mushroom(), MachiningMode.ThreeAxis);
        Assert.Equal(6, candidates.Count);

        var up = candidates.Single(c => c.PartAxis == Vector3.UnitZ);
        var down = candidates.Single(c => c.PartAxis == -Vector3.UnitZ);
        Assert.Equal(5000, up.UnreachableArea, 0);
        Assert.Equal(3700, down.UnreachableArea, 0);
        Assert.Same(up, candidates[^1]);
        Assert.True(candidates[0].UnreachableArea <= down.UnreachableArea);
    }

    [Fact]
    public void Orientation_ThreePlusOne_BoxIsFullyReachable()
    {
        var candidates = OrientationAdvisor.Suggest(TestMeshes.UnitCube(), MachiningMode.ThreePlusOne);
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(1, c.ReachableFraction, 9));
    }
}
