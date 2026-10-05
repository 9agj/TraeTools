namespace TraeCheckin;

/// <summary>签到结果判定：解析本次签到实际获得的积分。</summary>
public static class CheckinEvaluator
{
    /// <summary>
    /// claim 接口响应只含 code/message，不返回本次所得积分；
    /// 单日奖励需从 status 接口读取：credits 为基础签到所得（实测 150），
    /// extra_credits 是连续签到加成（实测 50），但**仅会员到账**。
    /// 非会员只计算 credits；会员才把 extra_credits 一并计入本次所得。
    /// 仅当状态为已签到且 code=0 时数值可信；否则返回 0。
    /// </summary>
    public static double ResolveGainedCredits(CheckinStatus? statusAfterClaim, bool isMember = false)
        => statusAfterClaim != null && statusAfterClaim.code == 0 && statusAfterClaim.checked_in
            ? statusAfterClaim.credits + (isMember ? statusAfterClaim.extra_credits : 0)
            : 0;
}
