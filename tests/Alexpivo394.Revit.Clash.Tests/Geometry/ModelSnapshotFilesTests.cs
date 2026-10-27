using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class ModelSnapshotFilesTests
{
    [Test]
    public void PendingSuffix_IsReserved()
    {
        InTemporaryDirectory(root =>
            Should.Throw<ArgumentException>(() => ModelSnapshotFiles.Publish(Path.Combine(root, "snapshot.PENDING"), _ => { })));
    }

    [Test]
    public void TargetAppearsDuringWrite_IsPreserved()
    {
        InTemporaryDirectory(root =>
        {
            var target = Path.Combine(root, "snapshot");
            Should.Throw<IOException>(() => ModelSnapshotFiles.Publish(target, _ =>
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "keep.txt"), "other writer");
            }));
            File.ReadAllText(Path.Combine(target, "keep.txt")).ShouldBe("other writer");
            Directory.GetDirectories(root).Length.ShouldBe(1);
        });
    }

    [Test]
    public void FailedSecondFile_DoesNotPublishSnapshot()
    {
        InTemporaryDirectory(root =>
        {
            var target = Path.Combine(root, "snapshot");
            Should.Throw<IOException>(() => ModelSnapshotFiles.Publish(target, staging =>
            {
                File.WriteAllText(Path.Combine(staging, "model.tcg"), "header Complete=true");
                throw new IOException("Injected parameter file failure");
            }));

            Directory.Exists(target).ShouldBeFalse();
            Directory.GetDirectories(root).ShouldBeEmpty();
        });
    }

    [Test]
    public void Write_PublishesBothReadableFiles_WithoutReplacingExistingSnapshot()
    {
        InTemporaryDirectory(root =>
        {
            var target = Path.Combine(root, "snapshot");
            var model = new ModelGeometry(new ModelHeader("model", "model.rvt", 2022, DateTimeOffset.UtcNow,
                true, "Navisworks", 0, 0, 0), [], []);
            var parameters = new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>());

            ModelSnapshotFiles.Write(target, model, parameters);

            using (var geometry = File.OpenRead(Path.Combine(target, "model.tcg")))
                ModelGeometryFile.Read(geometry).Header.ShouldBe(model.Header);
            using (var parameterFile = File.OpenRead(Path.Combine(target, "model.tcp")))
                ModelParametersFile.Read(parameterFile).ByUniqueId.Count.ShouldBe(0);
            Should.Throw<IOException>(() => ModelSnapshotFiles.Write(target, model, parameters));
            Directory.GetDirectories(root).Length.ShouldBe(1);
        });
    }

    private static void InTemporaryDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "toadclash-snapshot-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
