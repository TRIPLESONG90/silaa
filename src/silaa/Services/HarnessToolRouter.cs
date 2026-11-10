using Microsoft.Extensions.AI;

namespace silaa.Services;

/// <summary>
/// Keeps ordinary turns small and predictable. Capabilities are enabled from the
/// user's recent request and the few skills selected for that request instead of
/// exposing the entire harness catalog on every model call.
/// </summary>
internal sealed class HarnessToolPlan
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Names => _names;
    public bool Includes(string name) => _names.Contains(name);
    public bool IncludesAny(params string[] names) => names.Any(_names.Contains);

    public IList<AITool> Select(IEnumerable<AITool> candidates) =>
        candidates.Where(tool => _names.Contains(tool.Name)).ToList();

    internal HarnessToolPlan Add(params string[] names)
    {
        foreach (var name in names) _names.Add(name);
        return this;
    }
}

internal static class HarnessToolRouter
{
    private static readonly string[] WorkspaceRead =
        ["list_files", "glob_files", "read_file", "inspect_file_metadata", "search_text"];
    private static readonly string[] WorkspaceWrite = ["write_file", "replace_text", "apply_edits"];
    private static readonly string[] Git = ["git_status", "git_diff"];
    private static readonly string[] Network = ["request_network_access", "security_status", "list_security_audit"];
    private static readonly string[] SkillRead = ["read_skill", "read_skill_reference"];
    private static readonly string[] SkillManage =
        ["list_local_skills", "read_skill", "read_skill_reference", "create_or_update_skill", "validate_skill", "install_skill"];

    internal static HarnessToolPlan Create(
        string requestContext, SkillSelection selection, bool allowBackgroundAutomation)
    {
        var context = requestContext.ToLowerInvariant();
        var selected = selection.SelectedContent.ToLowerInvariant();
        var plan = new HarnessToolPlan().Add("ask_user_question");

        var mentionsSkill = ContainsAny(context, "skill", "스킬");
        var managesSkill = mentionsSkill && ContainsAny(context,
            "install", "설치", "등록", "import", "가져", "create", "만들", "update", "수정", "검증", "validate", "목록", "list");
        if (managesSkill) plan.Add(SkillManage);
        else if (selection.Selected.Count > 0) plan.Add(SkillRead);

        var hasPath = context.Contains("\\") || context.Contains('/') || context.Contains("folder") || context.Contains("폴더");
        var workspaceRequest = selection.Selected.Count > 0 || hasPath || ContainsAny(context,
            "code", "코드", "file", "파일", "workspace", "프로젝트", "project", "build", "빌드", "test", "테스트",
            "read", "읽", "probe", "attachment", "첨부", "debug", "오류", "에러", "fix", "고쳐", "분석", "inspect", "재고", "품목", "조회", "목록");
        var mutation = ContainsAny(context,
            "create", "add", "edit", "update", "delete", "remove", "write", "fix", "refactor", "implement", "build",
            "만들", "추가", "수정", "삭제", "제거", "작성", "고쳐", "리팩터", "구현", "빌드");
        var execution = selection.Selected.Count > 0 || ContainsAny(context,
            "run", "execute", "command", "script", "build", "test", "install", "query", "조회", "실행", "명령", "스크립트", "빌드", "테스트", "설치", "재고", "품목");
        if (workspaceRequest) plan.Add(WorkspaceRead);
        if (mutation) plan.Add(WorkspaceWrite);
        if (execution) plan.Add("run_command");
        if (ContainsAny(context, "git", "commit", "diff", "release", "배포", "릴리즈")) plan.Add(Git);
        if (hasPath) plan.Add("list_allowed_directories", "request_folder_access");

        var needsPython = ContainsAny(context, "python", "pip", "venv", "파이썬") ||
                          ContainsAny(selected, "python", "pip ", "venv", ".py");
        if (needsPython) plan.Add("inspect_python_environment", "run_command");

        var needsDateTime = ContainsAny(context,
            "date", "time", "today", "tomorrow", "calendar", "schedule", "reminder", "날짜", "시간", "오늘", "내일", "일정", "예약", "알람");
        if (needsDateTime) plan.Add("get_current_datetime");

        var calendar = ContainsAny(context, "calendar", "일정", "약속", "appointment", "event");
        if (calendar) plan.Add("ask_calendar_question", "finalize_calendar_event");

        var documents = ContainsAny(context, "pdf", "docx", "xlsx", "ppt", "pptx", "document", "문서", "엑셀", "파워포인트");
        if (documents) plan.Add("read_document", "create_document");

        var projectContext = ContainsAny(context, "project", "프로젝트", "memory", "기억", "resource", "자료");
        if (projectContext) plan.Add("list_project_resources", "read_project_resource", "search_project_resources", "search_memory");

        var web = ContainsAny(context, "http://", "https://", "web", "웹", "online", "인터넷", "latest", "최신", "검색", "search");
        if (web) plan.Add("web_search", "fetch_url");
        var network = web || ContainsAny(selected, "http://", "https://", " api", "login", "network", "sdk");
        if (network) plan.Add(Network);

        var vault = ContainsAny(context, "vault", "secret", "token", "api key", "apikey", "키", "토큰", "비밀");
        if (vault) plan.Add("list_vault_entries", "run_command_with_vault", "request_vault_secret", "run_command");

        if (ContainsAny(context, "github", "onedrive", "connected app", "연결 앱", "앱 연결"))
            plan.Add("list_connected_apps", "read_connected_app", "search_connected_app", "write_connected_app");

        if (ContainsAny(context, "multi-agent", "multi agent", "멀티 에이전트", "delegate", "위임", "review agent"))
            plan.Add("delegate_task", "review_result");

        if (allowBackgroundAutomation && ContainsAny(context,
                "background", "백그라운드", "cron", "반복 작업", "예약 작업", "스케줄", "schedule", "reminder", "알람"))
            plan.Add("start_background_task", "create_reminder", "list_background_tasks", "cancel_background_task",
                "create_cron_job", "list_cron_jobs", "set_cron_job_enabled", "delete_cron_job");

        return plan;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
