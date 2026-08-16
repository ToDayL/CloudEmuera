using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace CloudEmuera.RuntimeCompatibility.Tests;

[Trait("Category", "RuntimeBridge")]
public sealed class ChineseConfigMappingTests
{
    [Fact]
    public void SimplifiedChineseConfigNamesResolveToPinnedUpstreamCodes()
    {
        ConfigData config = ConfigData.Instance;
        var expected = new Dictionary<string, ConfigCode>
        {
            ["忽略大小写"] = ConfigCode.IgnoreCase,
            ["启用_Rename.csv"] = ConfigCode.UseRenameFile,
            ["启用_Replace.csv"] = ConfigCode.UseReplaceFile,
            ["启用鼠标"] = ConfigCode.UseMouse,
            ["显示菜单栏"] = ConfigCode.UseMenu,
            ["启用调试命令"] = ConfigCode.UseDebugCommand,
            ["允许多重启动"] = ConfigCode.AllowMultipleInstances,
            ["自动保存进度"] = ConfigCode.AutoSave,
            ["启用键盘宏"] = ConfigCode.UseKeyMacro,
            ["允许调整窗口高度"] = ConfigCode.SizableWindow,
            ["绘图界面"] = ConfigCode.TextDrawingMode,
            ["窗口宽度"] = ConfigCode.WindowX,
            ["窗口高度"] = ConfigCode.WindowY,
            ["窗口X坐标"] = ConfigCode.WindowPosX,
            ["窗口Y坐标"] = ConfigCode.WindowPosY,
            ["启动时固定窗口位置"] = ConfigCode.SetWindowPos,
            ["启动时将窗口最大化"] = ConfigCode.WindowMaximixed,
            ["记录日志的行数"] = ConfigCode.MaxLog,
            ["PRINTC并列数量"] = ConfigCode.PrintCPerLine,
            ["PRINTC文字数量"] = ConfigCode.PrintCLength,
            ["字体名称"] = ConfigCode.FontName,
            ["字体大小"] = ConfigCode.FontSize,
            ["每行高度"] = ConfigCode.LineHeight,
            ["每秒帧数"] = ConfigCode.FPS,
            ["最大SKIP帧数"] = ConfigCode.SkipFrame,
            ["滚动行数"] = ConfigCode.ScrollHeight,
            ["死循环超时警告(毫秒)"] = ConfigCode.InfiniteLoopAlertTime,
            ["最低显示警告等级"] = ConfigCode.DisplayWarningLevel,
            ["加载时显示报告"] = ConfigCode.DisplayReport,
            ["加载时解析参数"] = ConfigCode.ReduceArgumentOnLoad,
            ["忽略未被调用过的函数"] = ConfigCode.IgnoreUncalledFunction,
            ["函数未找到时的警告处理"] = ConfigCode.FunctionNotFoundWarning,
            ["函数未被调用时的警告处理"] = ConfigCode.FunctionNotCalledWarning,
            ["使用调试指令时改变MASTER的名字"] = ConfigCode.ChangeMasterNameIfDebug,
            ["不对按钮行折返换行"] = ConfigCode.ButtonWrap,
            ["检索子目录"] = ConfigCode.SearchSubdirectory,
            ["按文件名顺序读取"] = ConfigCode.SortWithFilename,
            ["最后更新代码"] = ConfigCode.LastKey,
            ["使用存档数量"] = ConfigCode.SaveDataNos,
            ["显示eramaker兼容性相关警告"] = ConfigCode.WarnBackCompatibility,
            ["允许重写系统函数"] = ConfigCode.AllowFunctionOverloading,
            ["重写系统函数时显示警告"] = ConfigCode.WarnFunctionOverloading,
            ["关联文本编辑器"] = ConfigCode.TextEditor,
            ["编辑器运行参数"] = ConfigCode.EditorType,
            ["编辑器运行参数值"] = ConfigCode.EditorArgument,
            ["重复定义非事件函数时显示警告"] = ConfigCode.WarnNormalFunctionOverloading,
            ["执行未能解析的行"] = ConfigCode.CompatiErrorLine,
            ["CALLNAME空字符串时代入NAME"] = ConfigCode.CompatiCALLNAME,
            ["在sav文件夹中创建存档"] = ConfigCode.UseSaveFolder,
            ["伪变量RAND符合eramaker规范"] = ConfigCode.CompatiRAND,
            ["DRAWLINE总是在新行进行"] = ConfigCode.CompatiDRAWLINE,
            ["函数、属性大小写敏感"] = ConfigCode.CompatiFunctionNoignoreCase,
            ["使用全角空格填充空白区域"] = ConfigCode.SystemAllowFullSpace,
            ["重现ver1739前的非按钮折返"] = ConfigCode.CompatiLinefeedAs1739,
            ["内部使用东亚语言"] = ConfigCode.useLanguage,
            ["允许CALL调用事件函数"] = ConfigCode.CompatiCallEvent,
            ["使用SP角色"] = ConfigCode.CompatiSPChara,
            ["以二进制形式保存存档"] = ConfigCode.SystemSaveInBinary,
            ["用户函数允许省略全部参数"] = ConfigCode.CompatiFuncArgOptional,
            ["用户参数自动补充TOSTR"] = ConfigCode.CompatiFuncArgAutoConvert,
            ["展开FORM中的三连记号"] = ConfigCode.SystemIgnoreTripleSymbol,
            ["不展开FORM中的三连记号"] = ConfigCode.SystemIgnoreTripleSymbol,
            ["TIMES的计算符合eramaker规范"] = ConfigCode.TimesNotRigorousCalculation,
            ["禁用角色变量的参数自动补全"] = ConfigCode.SystemNoTarget,
            ["字符串变量赋值时强制使用字符串"] = ConfigCode.SystemIgnoreStringSet,
            ["金钱单位"] = ConfigCode.MoneyLabel,
            ["单位位置"] = ConfigCode.MoneyFirst,
            ["启动时显示文字"] = ConfigCode.LoadLabel,
            ["出售物品数"] = ConfigCode.MaxShopItem,
            ["系统菜单0"] = ConfigCode.TitleMenuString0,
            ["系统菜单1"] = ConfigCode.TitleMenuString1,
            ["COM_ABLE初始值"] = ConfigCode.ComAbleDefault,
            ["污秽初始值"] = ConfigCode.StainDefault,
            ["超时显示"] = ConfigCode.TimeupLabel,
            ["EXPLV初始值"] = ConfigCode.ExpLvDef,
            ["PALAMLV初始值"] = ConfigCode.PalamLvDef,
            ["PBAND初始值"] = ConfigCode.pbandDef,
            ["RELATION初始值"] = ConfigCode.RelationDef,
            ["启动时显示调试窗口"] = ConfigCode.DebugShowWindow,
            ["启动窗口最前端显示"] = ConfigCode.DebugWindowTopMost,
            ["调试窗口宽度"] = ConfigCode.DebugWindowWidth,
            ["调试窗口高度"] = ConfigCode.DebugWindowHeight,
            ["指定调试窗口位置"] = ConfigCode.DebugSetWindowPos,
            ["调试窗口X坐标"] = ConfigCode.DebugWindowPosX,
            ["调试窗口Y坐标"] = ConfigCode.DebugWindowPosY,
        };

        foreach ((string key, ConfigCode code) in expected)
        {
            AConfigItem? item = config.GetItem(key);
            Assert.NotNull(item);
            Assert.Equal(code, item!.Code);
        }
    }
}
