using System.IO;
using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// The operator-facing message for <see cref="SDRIQStreamer.CWSkimmer.LaunchResult.TemplateIniNotFound"/>.
/// </summary>
/// <remarks>
/// Issue #75 folded the retired <c>DeviceNotFound</c> result into
/// TemplateIniNotFound, so this one formatter now has to tell four different
/// stories apart from the filesystem alone. Deriving them from the path rather
/// than from a second enum member is what let that member be deleted, which
/// makes these assertions the thing holding the subtraction in place.
/// </remarks>
public sealed class CwSkimmerLaunchMessageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetPath_SaysItIsUnset(string? path)
    {
        var message = CwSkimmerWorkflowService.FormatTemplateIniNotFound(path);

        Assert.Contains("is not set", message);
        Assert.DoesNotContain("not found at", message);
    }

    [Fact]
    public void FolderPath_SaysItIsAFolder()
    {
        // Issue #74's actual report: the operator entered the CW Skimmer folder
        // rather than the file inside it.
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var message = CwSkimmerWorkflowService.FormatTemplateIniNotFound(folder);

        Assert.Contains("is a folder", message);
        Assert.Contains(folder, message);
    }

    [Fact]
    public void ExistingFile_SaysCalibrationIsMissing_NotThatTheFileIsMissing()
    {
        // The branch that carries the retired DeviceNotFound case. Reporting a
        // file that plainly exists as "not found" is the regression to guard.
        var file = Path.GetTempFileName();
        try
        {
            var message = CwSkimmerWorkflowService.FormatTemplateIniNotFound(file);

            Assert.Contains("[Audio] calibration", message);
            Assert.DoesNotContain("not found at", message);
            Assert.Contains("device-diagnostic.txt", message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ChannelIniWriteFailure_BlamesTheWrite_NotTheCalibration()
    {
        // Codex deep audit of issue #75: with the template message made specific,
        // a calibrated master plus an unwritable artifacts folder was reporting
        // as a calibration problem and sending the operator to the wizard.
        var message = CwSkimmerWorkflowService.ChannelIniWriteFailedMessage;

        Assert.Contains("per-channel", message);
        Assert.Contains("writable", message);
        Assert.DoesNotContain("[Audio] calibration", message);
        Assert.DoesNotContain("Set Up Wizard", message);
    }

    [Fact]
    public void MissingFile_SaysItIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-here-75", "cwskimmer.ini");

        var message = CwSkimmerWorkflowService.FormatTemplateIniNotFound(missing);

        Assert.Contains("not found at", message);
        Assert.Contains(missing, message);
    }
}
