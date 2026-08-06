using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    // Invokes registered functions by name rather than by delegate, so a
    // reloaded assembly only needs to refresh the name->method dictionary;
    // entries already waiting in the queue keep working unchanged.
    //
    // Functions are split into two registries: "stable" ones (host-defined,
    // registered once at startup, never cleared) and "reloadable" ones
    // (core-defined, cleared and rebuilt on every load/reload). Keeping them
    // separate means a core reload can never wipe out host-defined commands.
    public sealed class Scheduler
    {
        private sealed class ScheduledCall
        {
            public required string FunctionName;
            public DateTime NextExecutionTime;
            public TimeSpan? RepeatInterval;
            public object?[] Args = Array.Empty<object?>();
        }

        private readonly List<ScheduledCall> _queue = new();
        private readonly Dictionary<string, MethodInfo> _stableFunctions = new();
        private readonly Dictionary<string, MethodInfo> _reloadableFunctions = new();

        public void RegisterStableFunctions(Assembly assembly)
        {
            foreach (var (name, method) in ScanSchedulableMethods(assembly))
            {
                _stableFunctions[name] = method;
            }
        }

        public void RegisterReloadableFunctions(Assembly assembly)
        {
            _reloadableFunctions.Clear();

            foreach (var (name, method) in ScanSchedulableMethods(assembly))
            {
                _reloadableFunctions[name] = method;
            }
        }

        public void ClearReloadableFunctions()
        {
            _reloadableFunctions.Clear();
        }

        public void ClearScheduledCalls()
        {
            _queue.Clear();
        }

        public IEnumerable<string> GetFunctionNames()
        {
            return _reloadableFunctions.Keys.Concat(_stableFunctions.Keys).Distinct();
        }

        public MethodInfo? GetMethod(string functionName)
        {
            if (_reloadableFunctions.TryGetValue(functionName, out var method))
            {
                return method;
            }

            return _stableFunctions.TryGetValue(functionName, out method) ? method : null;
        }

        // A C#-like signature string, e.g. "DrawCircle(XYZ center, double radius, double thickness = 2, double duration = -1)".
        public string? DescribeFunction(string functionName)
        {
            var method = GetMethod(functionName);
            return method == null ? null : FormatSignature(functionName, method);
        }

        public IEnumerable<string> DescribeAllFunctions()
        {
            return GetFunctionNames()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => FormatSignature(n, GetMethod(n)!));
        }

        // Takes the same "Name(arg1, arg2)" text the console accepts, so a
        // call in code reads exactly like what you'd type interactively.
        public bool Schedule(string commandText, DateTime nextExecutionTime, TimeSpan? repeatInterval = null)
        {
            if (!CommandText.TryParse(commandText, out var functionName, out var args, out var error))
            {
                Debug.WriteLine($"Scheduler: could not schedule '{commandText}': {error}");
                return false;
            }

            _queue.Add(new ScheduledCall
            {
                FunctionName = functionName,
                NextExecutionTime = nextExecutionTime,
                RepeatInterval = repeatInterval,
                Args = args
            });

            return true;
        }

        // Looks up and calls a registered function immediately - used both by
        // Pump() and by anything (ribbon, panel, console) that wants to trigger
        // a named command on demand rather than waiting for its scheduled time.
        // Every schedulable function returns bool; the result is logged as
        // "Success: Name" or "Fail: Name" right after it runs.
        //
        // uiApp: if provided and the target function's first parameter is
        // UIApplication, it's prepended to args automatically - so a function
        // needing live Revit API access (e.g. the current selection) can
        // declare it as a normal parameter without every caller needing to
        // supply it by hand. Pass null when no UIApplication is available
        // (e.g. Pump's scheduled calls).
        public bool Invoke(string functionName, UIApplication? uiApp, params object?[] args)
        {
            if (!_reloadableFunctions.TryGetValue(functionName, out var method) &&
                !_stableFunctions.TryGetValue(functionName, out method))
            {
                Debug.WriteLine($"Scheduler: no registered function named '{functionName}'.");
                return false;
            }

            var parameters = method.GetParameters();
            var callArgs = args;

            if (uiApp != null && parameters.Length > 0 && parameters[0].ParameterType == typeof(UIApplication))
            {
                callArgs = new object?[] { uiApp }.Concat(args).ToArray();
            }

            // MethodInfo.Invoke doesn't apply C# default parameter values for
            // omitted trailing arguments the way a normal compiled call does -
            // pad them in explicitly using each parameter's declared default.
            if (callArgs.Length < parameters.Length)
            {
                var padded = new object?[parameters.Length];
                callArgs.CopyTo(padded, 0);

                for (var i = callArgs.Length; i < parameters.Length; i++)
                {
                    padded[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                }

                callArgs = padded;
            }

            bool success;
            string? failureDetail = null;

            try
            {
                success = method.Invoke(null, callArgs) is true;
            }
            catch (Exception ex)
            {
                // Invoke wraps the target method's own exceptions in a
                // TargetInvocationException - unwrap to get the real cause.
                var actual = ex.InnerException ?? ex;
                Debug.WriteLine($"Scheduler: '{functionName}' threw: {ex}");
                failureDetail = $"{actual.GetType().Name}: {actual.Message}";
                success = false;
            }

            var quiet = method.GetCustomAttribute<SchedulableAttribute>()?.Quiet ?? false;

            if (!quiet)
            {
                var message = $"{(success ? "Success" : "Fail")}: {functionName}";

                if (failureDetail != null)
                {
                    message += $" ({failureDetail})";
                }

                Logger.Log(message);
            }

            return success;
        }

        public void Pump(DateTime now)
        {
            var due = _queue.Where(c => c.NextExecutionTime <= now).ToList();

            foreach (var call in due)
            {
                Invoke(call.FunctionName, null, call.Args);

                if (call.RepeatInterval.HasValue)
                {
                    call.NextExecutionTime = now + call.RepeatInterval.Value;
                }
                else
                {
                    _queue.Remove(call);
                }
            }
        }

        private static string FormatSignature(string name, MethodInfo method)
        {
            var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
            return $"{name}({parameters})";
        }

        private static string FormatParameter(ParameterInfo parameter)
        {
            var text = $"{FormatTypeName(parameter.ParameterType)} {parameter.Name}";

            if (parameter.HasDefaultValue)
            {
                text += $" = {FormatDefaultValue(parameter.DefaultValue)}";
            }

            return text;
        }

        private static string FormatTypeName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);

            if (underlying != null)
            {
                return FormatTypeName(underlying) + "?";
            }

            return type.Name switch
            {
                "Double" => "double",
                "Single" => "float",
                "Int32" => "int",
                "Int64" => "long",
                "Boolean" => "bool",
                "String" => "string",
                "Object" => "object",
                _ => type.Name
            };
        }

        private static string FormatDefaultValue(object? value)
        {
            return value switch
            {
                null => "null",
                string s => $"\"{s}\"",
                bool b => b ? "true" : "false",
                _ => value.ToString() ?? "null"
            };
        }

        private static IEnumerable<(string Name, MethodInfo Method)> ScanSchedulableMethods(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attribute = method.GetCustomAttribute<SchedulableAttribute>();

                    if (attribute != null)
                    {
                        yield return (attribute.Name, method);
                    }
                }
            }
        }
    }
}
