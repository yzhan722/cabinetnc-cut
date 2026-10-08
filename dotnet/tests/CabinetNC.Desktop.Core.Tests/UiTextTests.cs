using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class UiTextTests
{
    [Fact]
    public void Chinese_ui_keeps_the_source_text()
    {
        UiText.SetEnglish(false);
        Assert.Equal("打开方案…", UiText.T("打开方案…"));
        Assert.Equal("密排", UiText.T("密排"));
        Assert.Equal("Kitchen-Side", UiText.T("Kitchen-Side"));
    }

    [Fact]
    public void English_ui_translates_chrome_and_keeps_part_names()
    {
        UiText.SetEnglish(true);
        try
        {
            Assert.Equal("Open package…", UiText.T("打开方案…"));
            Assert.Equal("Nest", UiText.T("密排"));
            Assert.Equal("Recuts", UiText.T("补板库"));
            Assert.Equal("Material & machine", UiText.T("板材与设备"));
            Assert.Equal("File(_F)", UiText.T("文件(_F)"));
            Assert.Equal("Kitchen-Side", UiText.T("Kitchen-Side"));
            var status = UiText.T("已排 3 件 · Kitchen");
            Assert.Contains("Kitchen", status);
            Assert.DoesNotContain("件", status);
            Assert.False(UiText.HasCjk(status));
            Assert.False(UiText.HasCjk(UiText.ToEnglish("预检通过")));
            Assert.Equal(
                "OmniCam project (*.db)|*.db|All files|*.*",
                UiText.ToEnglish("OmniCam 工程 (*.db)|*.db|所有文件|*.*"));
        }
        finally
        {
            UiText.SetEnglish(false);
        }
    }

    [Fact]
    public void Paper_text_is_english_even_while_the_ui_is_chinese()
    {
        UiText.SetEnglish(false);
        Assert.Equal("Print", UiText.ToEnglish("打印"));
        Assert.Equal("White Stipple", UiText.ToEnglish("白点"));
        Assert.False(UiText.HasCjk(UiText.ToEnglish("预检通过\n无已排工序 — 先密排并启用轮廓/钻孔/开槽")));
    }
}
