using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PerformanceMonitor.Services;

internal sealed class StartupTaskService
{
    internal const string TaskName = "Performance Monitor";
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var executablePath = GetExecutablePath();
            if (!HasCurrentRegistration(executablePath))
            {
                Register(executablePath);
            }
        }
        else
        {
            Remove();
        }
    }

    public void Remove()
    {
        dynamic service = Connect();
        dynamic root = service.GetFolder("\\");
        try
        {
            _ = root.GetTask(TaskName);
        }
        catch (Exception exception) when (IsTaskMissing(exception))
        {
            return;
        }

        root.DeleteTask(TaskName, 0);
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.GetFileName(executablePath).Equals("PerformanceMonitor.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The startup task can only be registered by PerformanceMonitor.exe.");
        }

        return executablePath;
    }

    private static bool HasCurrentRegistration(string executablePath)
    {
        dynamic service = Connect();
        dynamic root = service.GetFolder("\\");
        dynamic task;
        try
        {
            task = root.GetTask(TaskName);
        }
        catch (Exception exception) when (IsTaskMissing(exception))
        {
            return false;
        }

        dynamic definition = task.Definition;
        if (definition.Actions.Count != 1 || definition.Triggers.Count != 1)
        {
            return false;
        }

        dynamic action = definition.Actions.Item(1);
        dynamic trigger = definition.Triggers.Item(1);
        return action.Type == TaskActionExecute &&
               string.Equals((string)action.Path, executablePath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals((string)action.Arguments, "--start-minimized", StringComparison.Ordinal) &&
               trigger.Type == TaskTriggerLogon &&
               definition.Principal.RunLevel == TaskRunLevelHighest;
    }

    private static void Register(string executablePath)
    {

        var userId = WindowsIdentity.GetCurrent().Name;
        dynamic service = Connect();
        dynamic root = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);

        definition.RegistrationInfo.Description =
            "Starts Performance Monitor in the system tray when the current user signs in.";
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2;

        definition.Principal.UserId = userId;
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelHighest;

        dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
        trigger.Id = "CurrentUserLogon";
        trigger.UserId = userId;
        trigger.Enabled = true;

        dynamic action = definition.Actions.Create(TaskActionExecute);
        action.Id = "LaunchPerformanceMonitor";
        action.Path = executablePath;
        action.Arguments = "--start-minimized";
        action.WorkingDirectory = Path.GetDirectoryName(executablePath);

        _ = root.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            userId,
            null,
            TaskLogonInteractiveToken,
            null);
    }

    private static dynamic Connect()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be created.");
        service.Connect();
        return service;
    }

    private static bool IsTaskMissing(Exception exception) =>
        unchecked((uint)exception.HResult) is 0x80070002 or 0x80070003 or 0x8004130F;
}
