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

        /// <summary>See the task, read its conversation and watch its desktop.</summary>
        public const string TaskView = "TASK_VIEW";
        /// <summary>Write to the agent and interrupt it.</summary>
        public const string TaskPrompt = "TASK_PROMPT";
        /// <summary>The mouse and keyboard on the desktop, and its clipboard.</summary>
        public const string TaskDrive = "TASK_DRIVE";
        /// <summary>Rename, stop, start and delete it, and share it.</summary>
        public const string TaskManage = "TASK_MANAGE";
        /// <summary>The desktop's own: read its setup, exchange messages, call the model.</summary>
        public const string TaskRunAgent = "TASK_RUN_AGENT";
        /// <summary>The operator's own: start, stop and delete the desktop.</summary>
        public const string TaskOperateDesktop = "TASK_OPERATE_DESKTOP";
        /// <summary>The operator's own: make and remove the desktop kept ready for the team's next task.</summary>
        public const string TeamOperateSpare = "TEAM_OPERATE_SPARE";
        /// <summary>The notifier's own: email the people of a task when the agent's turn ends.</summary>
        public const string TaskNotify = "TASK_NOTIFY";
        /// <summary>The preparing desktop's own: read the environment's setup, with its variables, and say how it went.</summary>
        public const string EnvironmentRunSetup = "ENVIRONMENT_RUN_SETUP";
        /// <summary>The operator's own: make the machine that prepares an environment, save its disk and remove both.</summary>
        public const string EnvironmentOperateSetup = "ENVIRONMENT_OPERATE_SETUP";

        /// <summary>The Mac's own: take the source of a team's build, and hand back its log and its app.</summary>
        public const string TeamRunBuild = "TEAM_RUN_BUILD";
        /// <summary>The operator's own: ask the Mac to build, fail a build it never finished, and remove what nobody takes.</summary>
        public const string TeamOperateBuild = "TEAM_OPERATE_BUILD";

        public static readonly string[] All =
        [
            AdminView,
            TeamView, TeamViewUsage, TeamManageMembers, TeamManageSettings, TeamCreateEnvironment, TeamCreateTask,
            EnvironmentView, EnvironmentEdit,
            TaskView, TaskPrompt, TaskDrive, TaskManage, TaskRunAgent, TaskOperateDesktop, TeamOperateSpare, TaskNotify,
            EnvironmentRunSetup, EnvironmentOperateSetup,
            TeamRunBuild, TeamOperateBuild,
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
        /// <summary>Someone outside the team the task is shared with: they watch it.</summary>
        public const string TaskWatcher = "task_watcher";
        /// <summary>They watch it and write to the agent.</summary>
        public const string TaskPrompter = "task_prompter";
        /// <summary>They watch it, write to the agent and use the desktop's mouse and keyboard.</summary>
        public const string TaskDriver = "task_driver";
        public const string EnvironmentSetup = "environment_setup";
        /// <summary>The Mac that builds iPhone apps, for every team.</summary>
        public const string AppBuilder = "app_builder";
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

        seed.Permission(Permissions.TaskView, "See a task and watch its desktop", ResourceTypes.Task);
        seed.Permission(Permissions.TaskPrompt, "Write to the task's agent and interrupt it", ResourceTypes.Task);
        seed.Permission(Permissions.TaskDrive, "Use the desktop's mouse and keyboard", ResourceTypes.Task);
        seed.Permission(Permissions.TaskManage, "Rename, stop, start, delete and share a task", ResourceTypes.Task);
        seed.Permission(Permissions.TaskRunAgent, "Run the task's agent", ResourceTypes.Task);
        seed.Permission(Permissions.TaskOperateDesktop, "Start, stop and delete the task's desktop", ResourceTypes.Task);
        seed.Permission(Permissions.TeamOperateSpare, "Make and remove the team's spare desktop", ResourceTypes.Team);
        seed.Permission(Permissions.TaskNotify, "Email the task's people when the agent's turn ends", ResourceTypes.Task);
        seed.Permission(Permissions.EnvironmentRunSetup, "Do the environment's setup", ResourceTypes.Environment);
        seed.Permission(Permissions.EnvironmentOperateSetup, "Prepare the environment and keep its saved setup", ResourceTypes.Environment);

        seed.Permission(Permissions.TeamRunBuild, "Build the team's iPhone apps", ResourceTypes.Team);
        seed.Permission(Permissions.TeamOperateBuild, "Ask for the team's builds and remove old ones", ResourceTypes.Team);

        // The dashboard's numbers and every team's limits, and nothing inside a team: someone who should see a
        // team's tasks and desktops is invited to the team like anyone else.
        seed.Role(Roles.SystemAdmin, "System administrator").Can(Permissions.AdminView);
        seed.Role(Roles.DesktopOperator, "Desktop operator").Can(Permissions.TaskOperateDesktop, Permissions.TeamOperateSpare, Permissions.EnvironmentOperateSetup, Permissions.TeamOperateBuild);
        // The source of any team's build in flight, and nothing else of any team.
        seed.Role(Roles.AppBuilder, "The Mac that builds iPhone apps").Can(Permissions.TeamRunBuild);
        seed.Role(Roles.TaskNotifier, "Task notifier").Can(Permissions.TaskNotify);
        seed.Role(Roles.EnvironmentSetup, "The desktop preparing the environment").Can(Permissions.EnvironmentRunSetup);
        seed.Role(Roles.TeamMember, "Team member").Can(
            Permissions.TeamView, Permissions.TeamViewUsage, Permissions.TeamCreateEnvironment, Permissions.TeamCreateTask,
            Permissions.EnvironmentView, Permissions.EnvironmentEdit,
            Permissions.TaskView, Permissions.TaskPrompt, Permissions.TaskDrive, Permissions.TaskManage);
        seed.Role(Roles.TeamAdmin, "Team administrator").Can(Permissions.TeamManageMembers, Permissions.TeamManageSettings);
        seed.Role(Roles.TaskDesktop, "The task's desktop").Can(Permissions.TaskRunAgent);
        // A task shared with someone outside the team, granted on the task alone with an end the sharer chose.
        // Nothing here reaches the team, its environments or its other tasks, and none of them manages the task.
        seed.Role(Roles.TaskWatcher, "Watches a shared task").Can(Permissions.TaskView);
        seed.Role(Roles.TaskPrompter, "Writes to a shared task's agent").Can(Permissions.TaskView, Permissions.TaskPrompt);
        seed.Role(Roles.TaskDriver, "Uses a shared task's desktop").Can(Permissions.TaskView, Permissions.TaskPrompt, Permissions.TaskDrive);
    }
}
