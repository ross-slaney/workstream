using SqlOS.Fga.Configuration;

namespace Workstream.Api;

// The authorization model from docs/core-model.md: resource types, typed permissions and flat roles.
// A permission is RESOURCETYPE_ACTION and authorizes only nodes of that type; a role carries the
// permissions of each descendant type, so one grant on a team reaches every task and environment in it.
public static class WorkstreamAuthorization
{
    public static class ResourceTypes
    {
        public const string Root = "root";
        public const string Team = "team";
        public const string Environment = "environment";
        public const string Task = "task";
    }

    public static class Permissions
    {
        public const string AdminView = "ADMIN_VIEW";

        public const string TeamView = "TEAM_VIEW";
        public const string TeamViewUsage = "TEAM_VIEW_USAGE";
        public const string TeamManageMembers = "TEAM_MANAGE_MEMBERS";
        public const string TeamManageSettings = "TEAM_MANAGE_SETTINGS";
        public const string TeamCreateEnvironment = "TEAM_CREATE_ENVIRONMENT";
        public const string TeamCreateTask = "TEAM_CREATE_TASK";

        public const string EnvironmentView = "ENVIRONMENT_VIEW";
        public const string EnvironmentEdit = "ENVIRONMENT_EDIT";

        public const string TaskView = "TASK_VIEW";
        public const string TaskUse = "TASK_USE";
        public const string TaskManage = "TASK_MANAGE";
        /// <summary>The desktop's own: read its setup, exchange messages, call the model.</summary>
        public const string TaskRunAgent = "TASK_RUN_AGENT";
        /// <summary>The operator's own: start, stop and delete the desktop.</summary>
        public const string TaskOperateDesktop = "TASK_OPERATE_DESKTOP";
        /// <summary>The operator's own: make and remove the desktop kept ready for the team's next task.</summary>
        public const string TeamOperateSpare = "TEAM_OPERATE_SPARE";
        /// <summary>The notifier's own: email the people of a task when the agent's turn ends.</summary>
        public const string TaskNotify = "TASK_NOTIFY";

        public static readonly string[] All =
        [
            AdminView,
            TeamView, TeamViewUsage, TeamManageMembers, TeamManageSettings, TeamCreateEnvironment, TeamCreateTask,
            EnvironmentView, EnvironmentEdit,
            TaskView, TaskUse, TaskManage, TaskRunAgent, TaskOperateDesktop, TeamOperateSpare, TaskNotify,
        ];
    }

    public static class Roles
    {
        public const string SystemAdmin = "system_admin";
        public const string DesktopOperator = "desktop_operator";
        public const string TaskNotifier = "task_notifier";
        public const string TeamMember = "team_member";
        public const string TeamAdmin = "team_admin";
        public const string TaskDesktop = "task_desktop";
    }

    public static void Seed(SqlOSFgaSeedBuilder seed)
    {
        seed.ResourceType(ResourceTypes.Team, "Team");
        seed.ResourceType(ResourceTypes.Environment, "Environment");
        seed.ResourceType(ResourceTypes.Task, "Task");

        seed.Permission(Permissions.AdminView, "Use the system administrator's dashboard", ResourceTypes.Root);

        seed.Permission(Permissions.TeamView, "See the team", ResourceTypes.Team);
        seed.Permission(Permissions.TeamViewUsage, "See the team's usage", ResourceTypes.Team);
        seed.Permission(Permissions.TeamManageMembers, "Invite, remove and change people", ResourceTypes.Team);
        seed.Permission(Permissions.TeamManageSettings, "Change the team's settings", ResourceTypes.Team);
        seed.Permission(Permissions.TeamCreateEnvironment, "Add an environment", ResourceTypes.Team);
        seed.Permission(Permissions.TeamCreateTask, "Start a task", ResourceTypes.Team);

        seed.Permission(Permissions.EnvironmentView, "See an environment", ResourceTypes.Environment);
        seed.Permission(Permissions.EnvironmentEdit, "Change an environment", ResourceTypes.Environment);

        seed.Permission(Permissions.TaskView, "See a task", ResourceTypes.Task);
        seed.Permission(Permissions.TaskUse, "Talk to the agent and use the desktop", ResourceTypes.Task);
        seed.Permission(Permissions.TaskManage, "Rename, stop, start and delete a task", ResourceTypes.Task);
        seed.Permission(Permissions.TaskRunAgent, "Run the task's agent", ResourceTypes.Task);
        seed.Permission(Permissions.TaskOperateDesktop, "Start, stop and delete the task's desktop", ResourceTypes.Task);
        seed.Permission(Permissions.TeamOperateSpare, "Make and remove the team's spare desktop", ResourceTypes.Team);
        seed.Permission(Permissions.TaskNotify, "Email the task's people when the agent's turn ends", ResourceTypes.Task);

        // The dashboard's numbers and every team's limits, and nothing inside a team: someone who should see a
        // team's tasks and desktops is invited to the team like anyone else.
        seed.Role(Roles.SystemAdmin, "System administrator").Can(Permissions.AdminView);
        seed.Role(Roles.DesktopOperator, "Desktop operator").Can(Permissions.TaskOperateDesktop, Permissions.TeamOperateSpare);
        seed.Role(Roles.TaskNotifier, "Task notifier").Can(Permissions.TaskNotify);
        seed.Role(Roles.TeamMember, "Team member").Can(
            Permissions.TeamView, Permissions.TeamViewUsage, Permissions.TeamCreateEnvironment, Permissions.TeamCreateTask,
            Permissions.EnvironmentView, Permissions.EnvironmentEdit,
            Permissions.TaskView, Permissions.TaskUse, Permissions.TaskManage);
        seed.Role(Roles.TeamAdmin, "Team administrator").Can(Permissions.TeamManageMembers, Permissions.TeamManageSettings);
        seed.Role(Roles.TaskDesktop, "The task's desktop").Can(Permissions.TaskRunAgent);
    }
}
