using System.IO;
using System.Security.Principal;

namespace HardwareWidget;

internal static class ElevatedTaskHelper
{
    public const string TaskName = "HardwareWidget";
    public const string FromTaskArg = "--from-task";

    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskInstancesParallel = 0;

    public static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string ResolveBaseDirectory()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(current);
        for (int i = 0; i < 7 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HardwareWidget.csproj")) ||
                File.Exists(Path.Combine(dir.FullName, "widget_settings.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return current;
    }

    public static string ResolveExePath(string baseDir)
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            File.Exists(processPath) &&
            !processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }

        string appDirExe = Path.Combine(AppContext.BaseDirectory, "HardwareWidget.exe");
        if (File.Exists(appDirExe))
        {
            return Path.GetFullPath(appDirExe);
        }

        string releaseExe = Path.Combine(baseDir, @"bin\Release\net8.0-windows\HardwareWidget.exe");
        if (File.Exists(releaseExe))
        {
            return Path.GetFullPath(releaseExe);
        }

        return string.Empty;
    }

    public static bool TryRunElevatedTask(string expectedExePath)
    {
        if (string.IsNullOrEmpty(expectedExePath)) return false;

        try
        {
            Type? serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return false;

            dynamic taskService = Activator.CreateInstance(serviceType)!;
            taskService.Connect();

            dynamic rootFolder = taskService.GetFolder("\\");
            dynamic task = rootFolder.GetTask(TaskName);
            if (!(bool)task.Enabled) return false;

            dynamic def = task.Definition;
            if ((int)def.Principal.RunLevel != TaskRunLevelHighest) return false;
            if ((int)def.Actions.Count < 1) return false;

            dynamic firstAction = def.Actions.Item(1);
            string? actionPath = firstAction.Path as string;
            if (string.IsNullOrWhiteSpace(actionPath)) return false;

            string normalizedActionPath = Path.GetFullPath(actionPath.Trim().Trim('"'));
            string normalizedExpectedPath = Path.GetFullPath(expectedExePath);
            if (!string.Equals(normalizedActionPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            dynamic? runningTask = task.Run(null);
            return runningTask != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterOrUpdateTask(string exePath, string workingDir, bool startWithWindows)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? userSid = identity.User?.Value;
            if (string.IsNullOrEmpty(userSid)) return false;

            Type? serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return false;

            dynamic taskService = Activator.CreateInstance(serviceType)!;
            taskService.Connect();

            dynamic rootFolder = taskService.GetFolder("\\");
            dynamic taskDef = taskService.NewTask(0);
            taskDef.RegistrationInfo.Description =
                "Inicia HardwareWidget con permisos de Administrador sin solicitar UAC en arranques posteriores.";

            taskDef.Principal.UserId = userSid;
            taskDef.Principal.LogonType = TaskLogonInteractiveToken;
            taskDef.Principal.RunLevel = TaskRunLevelHighest;

            taskDef.Settings.Enabled = true;
            taskDef.Settings.AllowDemandStart = true;
            taskDef.Settings.DisallowStartIfOnBatteries = false;
            taskDef.Settings.StopIfGoingOnBatteries = false;
            taskDef.Settings.ExecutionTimeLimit = "PT0S";
            taskDef.Settings.MultipleInstances = TaskInstancesParallel;
            taskDef.Settings.StartWhenAvailable = true;
            taskDef.Settings.Priority = 5;

            if (startWithWindows)
            {
                dynamic trigger = taskDef.Triggers.Create(TaskTriggerLogon);
                trigger.UserId = userSid;
                trigger.Enabled = true;
            }

            dynamic action = taskDef.Actions.Create(TaskActionExec);
            action.Path = Path.GetFullPath(exePath);
            action.Arguments = FromTaskArg;
            action.WorkingDirectory = Path.GetFullPath(workingDir);

            // Permite al usuario actual (incluso sin elevar) leer y ejecutar esta tarea bajo demanda sin UAC
            string sddl = $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{userSid})";
            try
            {
                rootFolder.RegisterTaskDefinition(
                    TaskName,
                    taskDef,
                    TaskCreateOrUpdate,
                    null,
                    null,
                    TaskLogonInteractiveToken,
                    sddl);
            }
            catch
            {
                // Respaldo con descriptor predeterminado para conservar al menos el arranque automático elevado
                rootFolder.RegisterTaskDefinition(
                    TaskName,
                    taskDef,
                    TaskCreateOrUpdate,
                    null,
                    null,
                    TaskLogonInteractiveToken,
                    null);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
