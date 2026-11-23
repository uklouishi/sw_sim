using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>对局界面：自检（模拟点击）。</summary>
public partial class GameScreen
{
    // ================================================================ 自检

    private int _selfTestDone;
    private int _selfTestFailures;

    /// <summary>像玩家一样点击界面做出选择，检查点击能对应到选项（用于无人值守的自动检查）。</summary>
    private void SelfTestStep()
    {
        var d = _decision!;
        _selfTestDone++;
        var controls = _controls.ToList();
        if (IsMulti(d))
        {
            foreach (var c in controls.Where(c => c.Highlight == CardHighlight.Selectable))
                if (_multi.Count < d.MinPicks) OnClicked(c);
            // 牌库里查看的卡等不在场上的卡：用选项按钮选。
            for (int i = 0; i < d.Options.Count && _multi.Count < d.MinPicks; i++)
                if (!_multi.Contains(i) && (d.Options[i].CardInstanceId is not int oid || _view?.FindCard(oid) is null or { IsHidden: true }))
                    _multi.Add(i);
            if (_multi.Count != d.MinPicks) SelfTestFail(d, $"点卡后选中 {_multi.Count} 张，应为 {d.MinPicks}");
            Submit(_multi.OrderBy(x => x).ToArray());
            return;
        }
        if (d.Type == DecisionType.MainAction)
        {
            foreach (var h in controls.Where(c => c.Zone == ZoneType.Hand && c.Highlight == CardHighlight.Selectable))
            {
                OnClicked(h);
                if (!ReferenceEquals(_armed, h)) continue;
                var dest = _controls.FirstOrDefault(c => c.Highlight == CardHighlight.Target && c.Card == null);
                if (dest != null)
                {
                    OnClicked(dest);
                    if (_decision != null) SelfTestFail(d, "点了绿框格子但没有出牌");
                    return;
                }
                OnClicked(h);
            }
            Submit(d.Options.ToList().FindIndex(o => o.IsPass));
            return;
        }
        var first = controls.OrderBy(c => c.GetParent() is HFlowContainer ? 0 : 1).FirstOrDefault(c => c.Highlight == CardHighlight.Selectable);
        if (first != null)
        {
            OnClicked(first);
            if (_decision != null && _armedOptions.Count > 0) Submit(_armedOptions[0]);
            if (_decision != null) SelfTestFail(d, "点了黄框的卡但没有做出选择");
            return;
        }
        Submit(0);
    }

    private void SelfTestFail(Decision d, string why)
    {
        _selfTestFailures++;
        GD.PrintErr($"SELFTEST FAIL #{d.Sequence} {d.Type}: {why}");
    }
}
