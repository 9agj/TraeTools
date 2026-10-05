namespace TraeCheckin;

/// <summary>
/// 账号存储辅助：激活账号、启用列表、增删账号与 DeviceId 兜底填充。
/// 直接操作传入的 AppConfig（持久化由调用方负责，UI 层 Save）。
/// </summary>
public class AccountStore
{
    private readonly AppConfig _config;

    public AccountStore(AppConfig config)
    {
        _config = config;
        if (_config.Accounts.Count > 0 && string.IsNullOrEmpty(_config.ActiveAccountId))
            _config.ActiveAccountId = _config.Accounts[0].Id;
    }

    /// <summary>当前激活账号（无激活 Id 或 Id 失效时回退第一个）。</summary>
    public TraeAccount ActiveAccount
    {
        get
        {
            if (_config.Accounts.Count == 0) throw new InvalidOperationException("尚无账号");
            var byId = _config.Accounts.FirstOrDefault(a => a.Id == _config.ActiveAccountId);
            if (byId != null) return byId;
            _config.ActiveAccountId = _config.Accounts[0].Id;
            return _config.Accounts[0];
        }
    }

    /// <summary>按添加顺序返回参与本地自动签到/云端部署的账号。</summary>
    public IEnumerable<TraeAccount> EnabledAccounts()
        => _config.Accounts.Where(a => a.Enabled);

    public void SetActive(string id)
    {
        if (_config.Accounts.Any(a => a.Id == id))
            _config.ActiveAccountId = id;
    }

    /// <summary>新增账号并设为激活（调用方负责 Login 与 Save）。</summary>
    public TraeAccount AddNew()
    {
        var acc = new TraeAccount { DeviceId = NewUniqueDeviceId(exceptId: null) };
        _config.Accounts.Add(acc);
        _config.ActiveAccountId = acc.Id;
        return acc;
    }

    /// <summary>删除账号；删空后重置激活 Id。返回是否删除成功。</summary>
    public bool Remove(string id)
    {
        int idx = _config.Accounts.FindIndex(a => a.Id == id);
        if (idx < 0) return false;
        _config.Accounts.RemoveAt(idx);
        if (_config.Accounts.Count == 0)
        {
            _config.ActiveAccountId = null;
        }
        else if (_config.ActiveAccountId == id)
        {
            _config.ActiveAccountId = _config.Accounts[0].Id;
        }
        return true;
    }

    /// <summary>
    /// 解析并回填账号唯一 UID（JWT data.id，跨登录会话恒定）。
    /// 凭账号现有 Token 反查；已回填/无 Token/解析失败则保持原值。幂等。
    /// </summary>
    public static string? ResolveUid(TraeAccount account)
    {
        if (!string.IsNullOrEmpty(account.AccountUid)) return account.AccountUid;
        if (string.IsNullOrEmpty(account.Token)) return null;
        account.AccountUid = TokenUtils.ParseAccountUid(account.Token);
        return account.AccountUid;
    }

    /// <summary>
    /// 在除 exceptId 外的账号中查找 UID 相同的账号（同一手机号重复添加检测）。
    /// 会顺手回填已存在账号的 AccountUid；未命中返回 null。
    /// </summary>
    public TraeAccount? FindAccountWithUid(string? uid, string exceptId)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        foreach (var a in _config.Accounts)
        {
            if (a.Id == exceptId) continue;
            if (ResolveUid(a) == uid) return a;
        }
        return null;
    }

    /// <summary>DeviceId 为空时填充与其它账号不重复的 16 位数字；非空保留。</summary>
    public void EnsureDeviceId(TraeAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.DeviceId))
            account.DeviceId = NewUniqueDeviceId(exceptId: account.Id);
    }

    /// <summary>
    /// 强制给单个账号换发全新的 16 位设备号（保证与其它账号不重复），并持久化返回新值。
    /// 用于该账号设备号被 Trae 风控标记（-9074 各位参与用户太多）时更换规避。
    /// </summary>
    public string RenewDeviceId(TraeAccount account)
    {
        string old = account.DeviceId;
        string fresh = NewUniqueDeviceId(exceptId: account.Id);
        int guard = 0;
        while (fresh == old && guard++ < 8)   // 极低概率撞回旧值，重试几次
            fresh = NewUniqueDeviceId(exceptId: account.Id);
        account.DeviceId = fresh;
        return fresh;
    }

    /// <summary>为全部账号逐个换发新设备号（互不重复）。返回换了几个；换号后需调用方 Save。</summary>
    public int RenewAllDeviceIds()
    {
        if (_config.Accounts.Count == 0) return 0;
        foreach (var acc in _config.Accounts)
        {
            // 逐个换发，NewUniqueDeviceId 内部会排除包括已被换号的其它账号，保证全局互不重复
            RenewDeviceId(acc);
        }
        return _config.Accounts.Count;
    }

    /// <summary>生成不与已存在账号冲突的 16 位设备号（多账号共用同一设备号会触发 9074）。</summary>
    private string NewUniqueDeviceId(string? exceptId)
    {
        var used = new HashSet<string>(_config.Accounts
            .Where(a => a.Id != exceptId)
            .Select(a => a.DeviceId));
        string id;
        do
        {
            id = GenerateDeviceId();
        } while (used.Contains(id));
        return id;
    }

    private static string GenerateDeviceId()
        => Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
}
