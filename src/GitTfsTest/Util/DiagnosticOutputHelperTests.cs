using System.Diagnostics;
using GitTfs.Util;
using GitTfs.Core;
using Xunit;
using Moq;

namespace GitTfs.Test.Util
{
    public class DiagnosticOutputHelperTests : BaseTest
    {
        [Fact]
        public void OutputDiagnosticInformation_WhenDebugOutputDisabled_ShouldNotOutput()
        {
            // Arrange
            var globals = new Globals { DebugOutput = false };
            var diagnosticHelper = new DiagnosticOutputHelper(globals);
            var mockRemote = new Mock<IGitTfsRemote>();
            
            var initialListenerCount = Trace.Listeners.Count;

            // Act
            diagnosticHelper.OutputDiagnosticInformation("test-command", mockRemote.Object);

            // Assert - no exception should be thrown
            Assert.Equal(initialListenerCount, Trace.Listeners.Count);
        }

        [Fact]
        public void OutputDiagnosticInformation_WhenDebugOutputEnabled_ShouldNotThrowException()
        {
            // Arrange
            var mockRepository = new Mock<IGitRepository>();
            mockRepository.Setup(r => r.GitDir).Returns("/test/repo/.git");
            mockRepository.Setup(r => r.GetConfig(It.IsAny<string>())).Returns((string)null);
            mockRepository.Setup(r => r.HasRef(It.IsAny<string>())).Returns(false);
            
            var mockRemote = new Mock<IGitTfsRemote>();
            mockRemote.Setup(r => r.Id).Returns("default");
            mockRemote.Setup(r => r.TfsUrl).Returns("https://tfs.example.com:8080/tfs");
            mockRemote.Setup(r => r.TfsRepositoryPath).Returns("$/Project/Main");
            mockRemote.Setup(r => r.RemoteRef).Returns("refs/remotes/tfs/default");
            mockRemote.Setup(r => r.MaxChangesetId).Returns(12345);
            mockRemote.Setup(r => r.MaxCommitHash).Returns("abc123def456");
            mockRemote.Setup(r => r.TfsUsername).Returns((string)null);
            mockRemote.Setup(r => r.Repository).Returns(mockRepository.Object);

            var globals = new Globals
            {
                DebugOutput = true,
                CommandLineRun = "git tfs fetch --debug",
                Repository = mockRepository.Object
            };
            
            var diagnosticHelper = new DiagnosticOutputHelper(globals);

            // Act & Assert - should not throw
            var exception = Record.Exception(() => 
                diagnosticHelper.OutputDiagnosticInformation("fetch", mockRemote.Object));
            
            Assert.Null(exception);
        }

        [Fact]
        public void OutputDiagnosticInformation_WithNullRemote_ShouldNotThrowException()
        {
            // Arrange
            var mockRepository = new Mock<IGitRepository>();
            mockRepository.Setup(r => r.GitDir).Returns("/test/repo/.git");
            mockRepository.Setup(r => r.GetConfig(It.IsAny<string>())).Returns((string)null);
            
            var globals = new Globals
            {
                DebugOutput = true,
                CommandLineRun = "git tfs test",
                Repository = mockRepository.Object
            };
            
            var diagnosticHelper = new DiagnosticOutputHelper(globals);

            // Act & Assert - should not throw even with null remote
            var exception = Record.Exception(() => 
                diagnosticHelper.OutputDiagnosticInformation("test", null));
            
            Assert.Null(exception);
        }
    }
}
