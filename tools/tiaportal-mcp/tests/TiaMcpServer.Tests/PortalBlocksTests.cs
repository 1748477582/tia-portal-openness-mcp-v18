using System;
using System.IO;
using System.Text;
using Xunit;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// G6: Unit tests for pure-logic helper methods that don't need a TIA Portal instance.
    /// </summary>
    public class PortalBlocksTests
    {
        #region ExtractBlockNameFromImportXml

        [Fact]
        public void ExtractBlockNameFromXml_FB_ReturnsName()
        {
            var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V18"" />
  <SW.Blocks.FB ID=""0"" Version=""0"">
    <AttributeList>
      <Name>FB10804</Name>
      <Number>10804</Number>
    </AttributeList>
  </SW.Blocks.FB>
</Document>";
            var tmp = Path.GetTempFileName() + ".xml";
            File.WriteAllText(tmp, xml, Encoding.UTF8);

            var name = Portal.ExtractBlockNameFromImportXml(tmp);
            Assert.Equal("FB10804", name);

            File.Delete(tmp);
        }

        [Fact]
        public void ExtractBlockNameFromXml_FC_ReturnsName()
        {
            var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V18"" />
  <SW.Blocks.FC ID=""0"" Version=""0"">
    <AttributeList>
      <Name>FC100</Name>
    </AttributeList>
  </SW.Blocks.FC>
</Document>";
            var tmp = Path.GetTempFileName() + ".xml";
            File.WriteAllText(tmp, xml, Encoding.UTF8);

            var name = Portal.ExtractBlockNameFromImportXml(tmp);
            Assert.Equal("FC100", name);

            File.Delete(tmp);
        }

        [Fact]
        public void ExtractBlockNameFromXml_DB_ReturnsName()
        {
            var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V18"" />
  <SW.Blocks.DB ID=""0"" Version=""0"">
    <AttributeList>
      <Name>DB10011</Name>
    </AttributeList>
  </SW.Blocks.DB>
</Document>";
            var tmp = Path.GetTempFileName() + ".xml";
            File.WriteAllText(tmp, xml, Encoding.UTF8);

            var name = Portal.ExtractBlockNameFromImportXml(tmp);
            Assert.Equal("DB10011", name);

            File.Delete(tmp);
        }

        [Fact]
        public void ExtractBlockNameFromXml_NoName_FallsBackToFilename()
        {
            var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V18"" />
  <SW.Blocks.FB ID=""0"" Version=""0"">
    <AttributeList>
    </AttributeList>
  </SW.Blocks.FB>
</Document>";
            var tmp = Path.Combine(Path.GetTempPath(), "MyBlock.xml");
            File.WriteAllText(tmp, xml, Encoding.UTF8);

            var name = Portal.ExtractBlockNameFromImportXml(tmp);
            Assert.Equal("MyBlock", name); // fallback to filename

            File.Delete(tmp);
        }

        [Fact]
        public void ExtractBlockNameFromXml_FileNotFound_ReturnsFilenameStem()
        {
            var name = Portal.ExtractBlockNameFromImportXml(@"C:\nonexistent\FB10804.xml");
            Assert.Equal("FB10804", name); // fallback to filename stem
        }

        [Fact]
        public void ExtractBlockNameFromXml_InvalidXml_ReturnsFilenameStem()
        {
            var tmp = Path.GetTempFileName() + ".xml";
            File.WriteAllText(tmp, "not valid xml", Encoding.UTF8);

            var name = Portal.ExtractBlockNameFromImportXml(tmp);
            Assert.Equal(Path.GetFileNameWithoutExtension(tmp), name);

            File.Delete(tmp);
        }

        #endregion

        #region ValidateBlockName

        [Fact]
        public void ValidateBlockName_Valid_DoesNotThrow()
        {
            var ex = Record.Exception(() => Portal.ValidateBlockName("FB10804", "test"));
            Assert.Null(ex);
        }

        [Fact]
        public void ValidateBlockName_ValidWithUnderscore_DoesNotThrow()
        {
            var ex = Record.Exception(() => Portal.ValidateBlockName("FB_MyBlock_1", "test"));
            Assert.Null(ex);
        }

        [Fact]
        public void ValidateBlockName_Empty_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("", "test"));
        }

        [Fact]
        public void ValidateBlockName_Whitespace_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("   ", "test"));
        }

        [Fact]
        public void ValidateBlockName_TooLong_Throws()
        {
            var longName = new string('A', 65);
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName(longName, "test"));
        }

        [Fact]
        public void ValidateBlockName_ContainsSpace_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("FB 10804", "test"));
        }

        [Fact]
        public void ValidateBlockName_ContainsSlash_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("FB/10804", "test"));
        }

        [Fact]
        public void ValidateBlockName_ContainsBackslash_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("FB\\10804", "test"));
        }

        [Fact]
        public void ValidateBlockName_ContainsColon_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("FB:10804", "test"));
        }

        [Fact]
        public void ValidateBlockName_ContainsControlChar_Throws()
        {
            Assert.Throws<PortalException>(() => Portal.ValidateBlockName("FB\u000110804", "test"));
        }

        [Fact]
        public void ValidateBlockName_MaxLength_DoesNotThrow()
        {
            var name = new string('A', 64);
            var ex = Record.Exception(() => Portal.ValidateBlockName(name, "test"));
            Assert.Null(ex);
        }

        #endregion

        #region UnwrapImportError

        [Fact]
        public void UnwrapImportError_SingleException_ReturnsTypeAndMessage()
        {
            var ex = new InvalidOperationException("test error");
            var result = Portal.UnwrapImportError(ex);
            Assert.Contains("InvalidOperationException", result);
            Assert.Contains("test error", result);
        }

        [Fact]
        public void UnwrapImportError_ChainedExceptions_ReturnsAllLayers()
        {
            var inner2 = new ArgumentException("deepest");
            var inner1 = new IOException("middle", inner2);
            var outer = new PortalException(PortalErrorCode.ImportFailed, "outer", null, inner1);

            var result = Portal.UnwrapImportError(outer);
            Assert.Contains("PortalException", result);
            Assert.Contains("IOException", result);
            Assert.Contains("ArgumentException", result);
            Assert.Contains("outer", result);
            Assert.Contains("middle", result);
            Assert.Contains("deepest", result);
        }

        [Fact]
        public void UnwrapImportError_DeepChain_TruncatesAt6()
        {
            Exception ex = new Exception("level7");
            for (int i = 6; i >= 1; i--)
                ex = new Exception($"level{i}", ex);

            var result = Portal.UnwrapImportError(ex);
            // Should contain at most 6 layers (depth limit)
            var parts = result.Split('|');
            Assert.True(parts.Length <= 6, $"Expected <=6 parts, got {parts.Length}: {result}");
        }

        #endregion
    }
}