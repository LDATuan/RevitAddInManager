using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Autodesk.Revit.UI;

namespace RevitAddinManager.DevPane;

/// <summary>
/// Creates a pane type whose constructor takes parameters, the way the add-in itself creates it.
/// </summary>
/// <remarks>
/// <para>
/// Panes are often composed at startup, e.g. <c>RegisterDockablePane(id, title, new MyProvider(container.Resolve&lt;MainPanel&gt;()))</c>
/// inside a method that receives <see cref="UIControlledApplication"/> and builds a DI container. A hot-loaded copy of the
/// assembly never ran that method. Dev Pane finds it by scanning IL and runs a copy of it in which:
/// </para>
/// <list type="bullet">
/// <item><c>RegisterDockablePane</c> hands the provider to Dev Pane instead of Revit. Revit remembers a pane ID even when it is
/// registered after startup, so letting the call through would make the next reload fail with "Cannot register the same
/// dockable pane ID more than once".</item>
/// <item>Subscriptions to Revit application events are recorded, so the pane's previous build can be unsubscribed when it is
/// replaced. Revit keeps those handlers natively; nothing else could find them again.</item>
/// </list>
/// <para>
/// Constructor arguments otherwise come from the Revit session (UIApplication, Document, ...) or from any
/// <see cref="IServiceProvider"/> the add-in keeps in a static field or property.
/// </para>
/// </remarks>
internal sealed class PaneActivator
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null))
        .ToDictionary(o => o.Value);

    private static readonly HashSet<string> RevitApplicationTypes = new HashSet<string>
    {
        "Autodesk.Revit.UI.UIControlledApplication",
        "Autodesk.Revit.UI.UIApplication",
        "Autodesk.Revit.ApplicationServices.ControlledApplication",
        "Autodesk.Revit.ApplicationServices.Application",
    };

    /// <summary>Events whose add accessor was redirected; indexed by the redirect stubs.</summary>
    private static readonly List<EventInfo> RedirectedEvents = new List<EventInfo>();

    /// <summary>The activator running a composer; composers run on Revit's thread, one at a time.</summary>
    private static PaneActivator current;

    private readonly UIApplication app;
    private readonly List<string> warnings;
    private readonly Type[] types;
    private readonly List<object> capturedProviders = new List<object>();
    private readonly List<(EventInfo Event, object Target, Delegate Handler)> subscriptions = new List<(EventInfo, object, Delegate)>();
    private UIControlledApplication controlledApplication;

    public PaneActivator(UIApplication app, Assembly assembly, List<string> warnings)
    {
        this.app = app;
        this.warnings = warnings;
        types = LoadableTypes(assembly);
    }

    public object Create(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(type);

        if (TryCreate(type, out var instance, out var missing)) return instance;

        foreach (var composer in FindComposers(type))
        {
            RunComposer(composer);
            // The very instance the add-in built, with whatever it set up on the way (theme, state).
            var captured = capturedProviders.FirstOrDefault(type.IsInstanceOfType);
            if (captured != null) return captured;
            if (TryCreate(type, out instance, out missing)) return instance;
        }

        throw new InvalidOperationException(
            $"Could not create {type.FullName}: no value for constructor parameter(s) {string.Join(", ", missing)}. " +
            "Dev Pane fills parameters from the Revit session (UIApplication, UIControlledApplication, Document, ...) and from " +
            "IServiceProvider instances kept in static fields or properties of the add-in, after running the add-in method that " +
            "constructs this type. Give the pane a public parameterless constructor, or keep its container in a static IServiceProvider.");
    }

    /// <summary>Unsubscribes the Revit event handlers the composer attached; called when the pane is replaced.</summary>
    public void DetachEvents()
    {
        foreach (var (ev, target, handler) in subscriptions)
        {
            try
            {
                ev.RemoveEventHandler(target, handler);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevPane] Unsubscribing {ev.Name} failed: {Unwrap(ex).Message}");
            }
        }

        subscriptions.Clear();
    }

    private bool TryCreate(Type type, out object instance, out List<string> missing)
    {
        instance = null;
        missing = new List<string>();
        foreach (var ctor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];
            var unresolved = new List<string>();
            for (var i = 0; i < parameters.Length; i++)
            {
                args[i] = Resolve(parameters[i].ParameterType);
                if (args[i] == null) unresolved.Add($"{parameters[i].ParameterType.Name} {parameters[i].Name}");
            }

            if (unresolved.Count == 0)
            {
                instance = ctor.Invoke(args);
                return true;
            }

            if (missing.Count == 0) missing = unresolved;
        }

        return false;
    }

    private object Resolve(Type type)
    {
        var fromSession = ResolveFromSession(type);
        if (fromSession != null) return fromSession;

        foreach (var services in FindServiceProviders())
        {
            try
            {
                var service = services.GetService(type);
                if (service != null) return service;
            }
            catch (Exception ex)
            {
                warnings.Add($"Resolving {type.Name} from the add-in's IServiceProvider failed: {Unwrap(ex).Message}");
            }
        }

        return null;
    }

    private object ResolveFromSession(Type type)
    {
        if (type == typeof(UIApplication)) return app;
        if (type == typeof(UIControlledApplication)) return ControlledApplication;
        if (type == typeof(Autodesk.Revit.ApplicationServices.Application)) return app.Application;
        if (type == typeof(Autodesk.Revit.ApplicationServices.ControlledApplication)) return ControlledApplication.ControlledApplication;
        if (type == typeof(UIDocument)) return app.ActiveUIDocument;
        if (type == typeof(Autodesk.Revit.DB.Document)) return app.ActiveUIDocument?.Document;
        return null;
    }

    /// <summary>
    /// UIControlledApplication has no public constructor; the internal one wraps a live UIApplication and behaves like the
    /// startup instance (its events attach to the session).
    /// </summary>
    private UIControlledApplication ControlledApplication => controlledApplication ??= (UIControlledApplication)typeof(UIControlledApplication)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(UIApplication) }, null)
        ?.Invoke(new object[] { app });

    private IEnumerable<IServiceProvider> FindServiceProviders()
    {
        foreach (var type in types)
        {
            if (type.ContainsGenericParameters) continue;
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                object value = null;
                try
                {
                    value = member switch
                    {
                        FieldInfo field when typeof(IServiceProvider).IsAssignableFrom(field.FieldType) => field.GetValue(null),
                        PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0 &&
                                                                     typeof(IServiceProvider).IsAssignableFrom(property.PropertyType) => property.GetValue(null),
                        _ => null,
                    };
                }
                catch
                {
                    // A static getter that throws is not a container we can use.
                }

                if (value is IServiceProvider services) yield return services;
            }
        }
    }

    /// <summary>Methods that construct <paramref name="type"/> or register a dockable pane, most specific first.</summary>
    private IEnumerable<MethodBase> FindComposers(Type type)
    {
        var constructing = new List<MethodBase>();
        var registering = new List<MethodBase>();
        foreach (var method in types.Where(t => t != type).SelectMany(t => t.GetMethods(AllDeclared).Cast<MethodBase>()))
        {
            var calls = Instructions(method).Select(i => i.Member).OfType<MethodBase>().ToList();
            if (calls.Any(c => c is ConstructorInfo && c.DeclaringType == type)) constructing.Add(method);
            else if (calls.Any(IsRegisterDockablePane)) registering.Add(method);
        }

        return constructing.Concat(registering).Where(CanInvoke);
    }

    private static bool CanInvoke(MethodBase method) =>
        !method.ContainsGenericParameters &&
        method.DeclaringType is { IsValueType: false, IsInterface: false } declaring &&
        (method.IsStatic || declaring.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null) &&
        method.GetParameters().All(p => RevitApplicationTypes.Contains(p.ParameterType.FullName));

    private void RunComposer(MethodBase method)
    {
        var name = $"{method.DeclaringType?.FullName}.{method.Name}";
        object target = null;
        object[] args;
        try
        {
            target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType, nonPublic: true);
            args = method.GetParameters().Select(p => ResolveFromSession(p.ParameterType)).ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not prepare {name} to compose the pane: {Unwrap(ex).Message}");
            return;
        }

        DynamicMethod isolated = null;
        try
        {
            isolated = Isolate(method);
        }
        catch (Exception ex)
        {
            warnings.Add($"Ran {name} unmodified ({ex.Message}): it registers its pane ID with Revit, so reloading it again may fail.");
        }

        var previous = current;
        current = this;
        try
        {
            if (isolated != null) isolated.Invoke(null, (method.IsStatic ? args : new[] { target }.Concat(args)).ToArray());
            else method.Invoke(target, args);
            Debug.WriteLine($"[DevPane] Composed the pane by running {name}, as the add-in does at startup.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Running {name} to compose the pane failed: {Unwrap(ex).Message}");
        }
        finally
        {
            current = previous;
        }
    }

    // ---- Redirect targets, called from isolated composers ----

    public static void CaptureRegisterDockablePane(UIControlledApplication application, DockablePaneId id, string title, IDockablePaneProvider provider) =>
        current?.capturedProviders.Add(provider);

    public static void CaptureRegisterDockablePaneUi(UIApplication application, DockablePaneId id, string title, IDockablePaneProvider provider) =>
        current?.capturedProviders.Add(provider);

    public static void AddRecordedHandler(object target, Delegate handler, int eventIndex)
    {
        var ev = RedirectedEvents[eventIndex];
        ev.AddEventHandler(target, handler);
        current?.subscriptions.Add((ev, target, handler));
    }

    // ---- IL ----

    private static bool IsRegisterDockablePane(MethodBase method) =>
        method.Name == "RegisterDockablePane" && RevitApplicationTypes.Contains(method.DeclaringType?.FullName ?? "");

    /// <summary>
    /// Copies <paramref name="method"/> into a DynamicMethod with RegisterDockablePane and Revit event subscriptions redirected.
    /// Tokens are re-issued for the dynamic scope; every redirect replaces a call with a call of the same stack shape and size,
    /// so branch offsets and exception clauses stay valid.
    /// </summary>
    private static DynamicMethod Isolate(MethodBase method)
    {
        var body = method.GetMethodBody() ?? throw new NotSupportedException("no method body");
        var il = body.GetILAsByteArray();
        var parameters = method.GetParameters().Select(p => p.ParameterType).ToList();
        if (!method.IsStatic) parameters.Insert(0, method.DeclaringType);
        var returnType = method is MethodInfo info ? info.ReturnType : typeof(void);

        var dynamicMethod = new DynamicMethod(method.Name + "_DevPane", returnType, parameters.ToArray(), method.DeclaringType, skipVisibility: true)
        {
            InitLocals = body.InitLocals,
        };
        var scope = dynamicMethod.GetDynamicILInfo();

        foreach (var instruction in Instructions(method))
        {
            int token;
            switch (instruction.Member)
            {
                case MethodBase called when IsRegisterDockablePane(called):
                    token = scope.GetTokenFor(Redirect(called.DeclaringType == typeof(UIApplication)
                        ? nameof(CaptureRegisterDockablePaneUi)
                        : nameof(CaptureRegisterDockablePane)).MethodHandle);
                    MakeStaticCall(il, instruction);
                    break;
                case MethodInfo called when called.Name.StartsWith("add_") && RevitApplicationTypes.Contains(called.DeclaringType?.FullName ?? "") &&
                                            EventFor(called) is { } ev:
                    token = scope.GetTokenFor(EventRedirect(called, ev));
                    MakeStaticCall(il, instruction);
                    break;
                case MethodBase called:
                    token = called.DeclaringType is { IsGenericType: true }
                        ? scope.GetTokenFor(called.MethodHandle, called.DeclaringType.TypeHandle)
                        : scope.GetTokenFor(called.MethodHandle);
                    break;
                case FieldInfo field:
                    token = field.DeclaringType == null
                        ? scope.GetTokenFor(field.FieldHandle)
                        : scope.GetTokenFor(field.FieldHandle, field.DeclaringType.TypeHandle);
                    break;
                case Type type:
                    token = scope.GetTokenFor(type.TypeHandle);
                    break;
                case string text:
                    token = scope.GetTokenFor(text);
                    break;
                default:
                    throw new NotSupportedException($"{instruction.OpCode.Name} at IL_{instruction.Offset:X4}");
            }

            BitConverter.GetBytes(token).CopyTo(il, instruction.OperandOffset);
        }

        var locals = SignatureHelper.GetLocalVarSigHelper();
        foreach (var local in body.LocalVariables) locals.AddArgument(local.LocalType, local.IsPinned);
        scope.SetLocalSignature(locals.GetSignature());
        scope.SetCode(il, body.MaxStackSize);
        if (body.ExceptionHandlingClauses.Count > 0) scope.SetExceptions(ExceptionSection(body, scope));
        return dynamicMethod;
    }

    private static MethodInfo Redirect(string name) => typeof(PaneActivator).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

    private static EventInfo EventFor(MethodInfo addMethod) =>
        addMethod.DeclaringType?.GetEvents().FirstOrDefault(e => e.GetAddMethod() == addMethod);

    /// <summary>A static stub (target, handler) that subscribes through <see cref="AddRecordedHandler"/>.</summary>
    private static DynamicMethod EventRedirect(MethodInfo addMethod, EventInfo ev)
    {
        int index;
        lock (RedirectedEvents)
        {
            index = RedirectedEvents.IndexOf(ev);
            if (index < 0)
            {
                RedirectedEvents.Add(ev);
                index = RedirectedEvents.Count - 1;
            }
        }

        var stub = new DynamicMethod("add_" + ev.Name + "_DevPane", typeof(void),
            new[] { addMethod.DeclaringType, ev.EventHandlerType }, typeof(PaneActivator), skipVisibility: true);
        var generator = stub.GetILGenerator();
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldc_I4, index);
        generator.Emit(OpCodes.Call, Redirect(nameof(AddRecordedHandler)));
        generator.Emit(OpCodes.Ret);
        return stub;
    }

    /// <summary>callvirt and call are both one byte with a 4-byte token; redirects are static, so they need call.</summary>
    private static void MakeStaticCall(byte[] il, Instruction instruction) => il[instruction.Offset] = (byte)OpCodes.Call.Value;

    private static byte[] ExceptionSection(MethodBody body, DynamicILInfo scope)
    {
        var clauses = body.ExceptionHandlingClauses;
        var size = clauses.Count * 24 + 4;
        var section = new List<byte> { 0x41, (byte)size, (byte)(size >> 8), (byte)(size >> 16) }; // fat EH table
        foreach (var clause in clauses)
        {
            var classTokenOrFilter = clause.Flags switch
            {
                ExceptionHandlingClauseOptions.Clause => scope.GetTokenFor(clause.CatchType.TypeHandle),
                ExceptionHandlingClauseOptions.Filter => clause.FilterOffset,
                _ => 0,
            };
            foreach (var value in new[] { (int)clause.Flags, clause.TryOffset, clause.TryLength, clause.HandlerOffset, clause.HandlerLength, classTokenOrFilter })
                section.AddRange(BitConverter.GetBytes(value));
        }

        return section.ToArray();
    }

    private readonly struct Instruction
    {
        public Instruction(int offset, OpCode opCode, int operandOffset, object member)
        {
            Offset = offset;
            OpCode = opCode;
            OperandOffset = operandOffset;
            Member = member;
        }

        public int Offset { get; }
        public OpCode OpCode { get; }
        public int OperandOffset { get; }

        /// <summary>The resolved method, field, type or string the token refers to.</summary>
        public object Member { get; }
    }

    /// <summary>Stands in for a token that cannot be resolved or copied; <see cref="Isolate"/> rejects it.</summary>
    private static readonly object Unsupported = new object();

    /// <summary>Instructions of a method body that carry a metadata token, with that token resolved.</summary>
    private static IEnumerable<Instruction> Instructions(MethodBase method)
    {
        byte[] il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            yield break;
        }

        if (il == null) yield break;
        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            var offset = position;
            short value = il[position++];
            if (value == 0xFE && position < il.Length) value = unchecked((short)(0xFE00 | il[position++]));
            if (!OpCodesByValue.TryGetValue(value, out var opCode)) yield break;

            var operandOffset = position;
            if (position + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, position);
                object member = null;
                try
                {
                    member = opCode.OperandType switch
                    {
                        OperandType.InlineMethod => module.ResolveMethod(token, typeArgs, methodArgs),
                        OperandType.InlineField => module.ResolveField(token, typeArgs, methodArgs),
                        OperandType.InlineType => module.ResolveType(token, typeArgs, methodArgs),
                        OperandType.InlineTok => module.ResolveMember(token, typeArgs, methodArgs),
                        OperandType.InlineString => module.ResolveString(token),
                        // calli carries a signature, which the copy cannot re-issue.
                        OperandType.InlineSig => Unsupported,
                        _ => null,
                    };
                }
                catch
                {
                    // Unresolvable references (missing dependency) are reported as an unsupported instruction when copying.
                    member = opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok
                        ? Unsupported
                        : null;
                }

                if (member != null) yield return new Instruction(offset, opCode, operandOffset, member);
            }

            position += OperandSize(opCode, il, position);
        }
    }

    private static int OperandSize(OpCode opCode, byte[] il, int position) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * (position + 4 <= il.Length ? BitConverter.ToInt32(il, position) : 0),
        _ => 4,
    };

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).ToArray();
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
}
