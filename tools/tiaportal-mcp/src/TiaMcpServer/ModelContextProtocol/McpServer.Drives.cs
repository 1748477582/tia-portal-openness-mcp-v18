using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.HW;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region drives (Startdrive / SINAMICS via PUBLIC Openness HW API; Acx role-model bootstrap kept as diagnostic reference)

        private static readonly string TiaBin =
            Environment.GetEnvironmentVariable("TIA_MCP_ACX_BIN")
            ?? @"C:\Program Files\Siemens\Automation\Portal V18\Bin";

        private static readonly string DriveDiagDir = @"C:\Temp\drive_diag";
        private static void DriveLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(DriveDiagDir);
                File.AppendAllText(Path.Combine(DriveDiagDir, "drive.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
            catch { }
        }

        private static bool _acxResolverAttached;
        private static void EnsureAcxResolver()
        {
            if (_acxResolverAttached) return;
            _acxResolverAttached = true;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var name = new AssemblyName(args.Name).Name;
                    var path = Path.Combine(TiaBin, name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                catch { }
                return null;
            };
        }

        private static Assembly ResolveAcxOpennessAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name.IndexOf("Acx.Openness", StringComparison.OrdinalIgnoreCase) >= 0)
                    return asm;
            }
            EnsureAcxResolver();
            var path = Path.Combine(TiaBin, "Siemens.MC.Drives.Acx.Openness.dll");
            if (!File.Exists(path)) throw new McpException($"Acx Openness assembly not found at {path}");
            return Assembly.LoadFrom(path);
        }

        // Explicitly load the Acx Openness + dependency assemblies so the RoleFactory / Role* types
        // are present in the AppDomain (they are NOT loaded automatically and are not in the public
        // Siemens.Engineering.Sinamics API). Idempotent.
        private static bool _acxLoaded;
        private static void LoadAcxAssemblies()
        {
            EnsureAcxResolver();
            if (_acxLoaded) return;
            _acxLoaded = true;
            foreach (var name in new[]
            {
                "Siemens.MC.Drives.Acx.Openness",
                "Siemens.MC.Drives.Acx.Common",
                "Siemens.MC.Drives.Acx.BusinessLogic",
                "Siemens.MC.Drives.Common.Openness",
                "Siemens.MC.Drives.Common",
            })
            {
                try
                {
                    var p = Path.Combine(TiaBin, name + ".dll");
                    if (File.Exists(p)) Assembly.LoadFrom(p);
                }
                catch (Exception ex) { DriveLog($"LoadAcx {name} failed: {ex.Message}"); }
            }
            // Siemens.Automation.Basics: load BY NAME so we get TIA's own copy (correct Type identity)
            // rather than a second LoadFrom(path) copy that would make GetService return null.
            try { Assembly.Load(new AssemblyName("Siemens.Automation.Basics")); }
            catch (Exception ex) { DriveLog($"Load Siemens.Automation.Basics failed: {ex.Message}"); }
            DriveLog("Acx assemblies load attempted");
        }

        /// <summary>
        /// Materialize the SINAMICS drive-object-container ROLE via the internal Acx Openness RoleFactory
        /// (the public Siemens.Engineering.Sinamics API is absent on this machine). Returns the
        /// RoleDriveObjectContainer (an IPropertyContainer exposing DriveObjects).
        /// </summary>
        private static object GetDriveContainerRole(string deviceItemPath, out object workingContext)
        {
            workingContext = null;
            var di = Portal.GetDeviceItem(deviceItemPath);
            if (di == null) throw new McpException($"Device item not found: {deviceItemPath}");
            DriveLog("device item resolved");

            LoadAcxAssemblies();

            // IStaticAccessHelper (needed by RoleFactory ctor)
            var sahType = SafeType("Siemens.MC.Drives.Acx.Openness.Interfaces.IStaticAccessHelper");
            if (sahType == null) throw new McpException("IStaticAccessHelper type not found in Acx Openness assembly.");
            var sah = TryGetService(di, sahType);
            if (sah == null)
            {
                var sahConcrete = SafeType("Siemens.MC.Drives.Acx.Openness.Helpers.StaticAccessHelper");
                if (sahConcrete != null)
                {
                    try { sah = Activator.CreateInstance(sahConcrete); } catch (Exception ex) { DriveLog($"StaticAccessHelper ctor failed: {ex.Message}"); }
                }
            }
            if (sah == null) throw new McpException("Cannot obtain IStaticAccessHelper (required by Acx RoleFactory). Startdrive/Acx Openness may be unavailable.");
            DriveLog("staticAccessHelper obtained");

            // IWorkingContext (needed by CreateRoleByRelationName). Acx role types implement
            // Siemens.Automation.Basics.IWorkingContextProvider (WorkingContext property). The source
            // object (DeviceItem / Project) implements it too — resolve the interface from the object's
            // own loaded interfaces to avoid Assembly.LoadFrom identity mismatches.
            object? wc = GetWorkingContext(di);
            DriveLog($"wc via GetWorkingContext(di) = {(wc == null ? "null" : "ok")}");
            if (wc == null)
            {
                try
                {
                    var projs = Portal.GetProjects();
                    var proj = (projs != null && projs.Count > 0) ? projs[0] : null;
                    wc = GetWorkingContext(proj);
                    DriveLog($"wc via GetWorkingContext(proj) = {(wc == null ? "null" : "ok")}");
                }
                catch (Exception ex) { DriveLog($"GetWorkingContext(proj) failed: {ex.Message}"); }
            }
            if (wc == null)
            {
                ProbeServices(di, "DI");
                // parent device (SINAMICS device) and parent DeviceItem
                try
                {
                    var devProp = di.GetType().GetProperty("Device", BindingFlags.Public | BindingFlags.Instance);
                    var dev = devProp?.GetValue(di);
                    if (dev != null) ProbeServices(dev, "DEVICE");
                    var parentProp = di.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
                    var parent = parentProp?.GetValue(di);
                    if (parent != null) ProbeServices(parent, "PARENT_DI");
                }
                catch (Exception ex) { DriveLog($"parent probe failed: {ex.Message}"); }
                try { var projs2 = Portal.GetProjects(); var proj2 = (projs2 != null && projs2.Count > 0) ? projs2[0] : null; if (proj2 != null) ProbeServices(proj2, "PROJ"); } catch (Exception ex) { DriveLog($"PROJ probe failed: {ex.Message}"); }
                throw new McpException("Cannot obtain IWorkingContext (required by Acx RoleFactory). Startdrive/Acx Openness may be unavailable.");
            }
            workingContext = wc;
            DriveLog("workingContext obtained");

            // RoleFactory
            var rfType = SafeType("Siemens.MC.Drives.Acx.Openness.RoleFactory");
            if (rfType == null) throw new McpException("RoleFactory type not found in Acx Openness assembly.");
            object rf;
            try { rf = Activator.CreateInstance(rfType, sah); }
            catch (Exception ex) { throw new McpException($"RoleFactory instantiation failed: {ex.Message}"); }
            DriveLog("roleFactory created");

            // Diagnostic: does IStaticAccessHelper expose a WorkingContext we could reuse?
            try
            {
                var sahT = sah?.GetType();
                var wcProp = sahT?.GetProperty("WorkingContext", BindingFlags.Public | BindingFlags.Instance);
                DriveLog($"sah type={sahT?.FullName} hasWorkingContextProp={(wcProp != null)}");
                if (wcProp != null && wc == null)
                {
                    try { var swc = wcProp.GetValue(sah); if (swc != null) { wc = swc; DriveLog("wc obtained from sah.WorkingContext"); } }
                    catch (Exception ex) { DriveLog($"sah.WorkingContext getter failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { DriveLog($"sah diag failed: {ex.Message}"); }

            // Resolve the core object: CreateRoleByRelationName wants Siemens.Automation.ObjectFrame.ICoreObject.
            // The DeviceItem may not be one directly — try to obtain the ICoreObject via IServiceProvider.
            object coreObject = di;
            var icoType = ResolveType("Siemens.Automation.ObjectFrame.ICoreObject");
            if (icoType != null)
            {
                if (icoType.IsInstanceOfType(di))
                {
                    DriveLog("coreObject: di IS ICoreObject (use di directly)");
                }
                else
                {
                    DriveLog("coreObject: di is NOT ICoreObject — attempting IServiceProvider.GetService(ICoreObject)");
                    if (di is System.IServiceProvider spc)
                    {
                        try
                        {
                            var co = spc.GetService(icoType);
                            if (co != null) { coreObject = co; DriveLog("coreObject obtained via IServiceProvider = ok"); }
                            else DriveLog("coreObject via IServiceProvider = null");
                        }
                        catch (Exception ex) { DriveLog($"coreObject IServiceProvider failed: {ex.Message}"); }
                    }
                    if (coreObject == di) DriveLog($"coreObject fallback: still di (icoType={icoType.FullName})");
                }
            }
            else
            {
                DriveLog("coreObject: icoType not resolvable — passing di as-is");
            }

            var createMethod = rfType.GetMethods()
                .FirstOrDefault(m => m.Name == "CreateRoleByRelationName" && m.GetParameters().Length == 4);
            if (createMethod == null) throw new McpException("CreateRoleByRelationName method not found on RoleFactory.");
            var relationName = "Siemens.Engineering.MC.Drives.DriveObjectContainer";
            object container;
            try { container = createMethod.Invoke(rf, new object[] { relationName, coreObject, wc, null }); }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.Message ?? ex.Message;
                throw new McpException($"CreateRoleByRelationName('{relationName}') failed: {inner}");
            }
            if (container == null) throw new McpException($"CreateRoleByRelationName('{relationName}') returned null.");
            DriveLog("drive container role resolved");
            return container;
        }

        /// <summary>Fail fast with a clear message when no TIA project is open, instead of hanging on a wedged connection.</summary>
        private static void RequireConnected()
        {
            if (!Portal.IsConnected())
                throw new McpException("Not connected to a TIA Portal project. Open the target project (e.g. 'test1') in TIA Portal and reconnect this MCP server, then retry.");
        }

        // ---- reflection helpers (no compile-time refs to absent shims) ----

        private static Type? SafeType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Resolve a Type by full name, robustly. Tries loaded assemblies first, then walks the
        /// assembly reference graph rooted at the Acx.Openness assembly (which references
        /// Siemens.Automation.Basics, Siemens.Automation.ObjectFrame, ...). Assembly.Load(an) returns
        /// TIA's own already-loaded copy when present, preserving Type identity so IServiceProvider /
        /// reflection Invoke match the types the live TIA service container expects.</summary>
        private static Type? ResolveType(string fullName)
        {
            var t = SafeType(fullName);
            if (t != null) return t;
            try
            {
                var root = ResolveAcxOpennessAssembly();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<Assembly>();
                queue.Enqueue(root); seen.Add(root.FullName ?? "");
                while (queue.Count > 0)
                {
                    var a = queue.Dequeue();
                    t = a.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                    foreach (var an in a.GetReferencedAssemblies())
                    {
                        try { var ra = Assembly.Load(an); if (ra != null && seen.Add(ra.FullName ?? "")) queue.Enqueue(ra); }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { DriveLog($"ResolveType({fullName}) failed: {ex.Message}"); }
            return null;
        }

        /// <summary>Invoke target.GetService(...) with a runtime Type. Tries the non-generic
        /// GetService(Type) overload first (no generic-constraint issues), then the generic
        /// GetService&lt;T&gt;() if present.</summary>
        private static object? TryGetService(object target, Type serviceType)
        {
            try
            {
                // non-generic: object GetService(Type)
                var ng = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
                if (ng != null) return ng.Invoke(target, new object[] { serviceType });
            }
            catch (Exception ex) { DriveLog($"TryGetService(non-generic)<{serviceType.FullName}> failed: {ex.GetType().Name}: {ex.Message}"); }
            try
            {
                var mi = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;
                var g = mi.MakeGenericMethod(serviceType);
                return g.Invoke(target, null);
            }
            catch (Exception ex)
            {
                DriveLog($"TryGetService<{serviceType.FullName}> failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Obtain the Siemens.Automation.Basics.IWorkingContext. DeviceItem/Project implement
        /// IEngineeringServiceProvider / System.IServiceProvider (NOT IWorkingContextProvider directly),
        /// so the working context is resolved via the non-generic IServiceProvider.GetService(typeof(IWorkingContext))
        /// — the generic GetService&lt;T&gt;() has a constraint that IWorkingContext violates.</summary>
        /// <summary>Resolve a Type by full name by walking the assembly reference graph of <paramref name="target"/>
        /// (Assembly.Load on each referenced assembly name). This returns TIA's own copy of the type
        /// (correct identity), avoiding Assembly.LoadFrom path-identity mismatches that make
        /// IServiceProvider.GetService return null.</summary>
        private static Type? ResolveTypeViaReferences(object target, string fullName)
        {
            try
            {
                var asm = target.GetType().Assembly;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<Assembly>();
                queue.Enqueue(asm); seen.Add(asm.FullName ?? "");
                while (queue.Count > 0)
                {
                    var a = queue.Dequeue();
                    var t = a.GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                    foreach (var an in a.GetReferencedAssemblies())
                    {
                        try { var ra = Assembly.Load(an); if (ra != null && seen.Add(ra.FullName ?? "")) queue.Enqueue(ra); }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { DriveLog($"ResolveTypeViaReferences({fullName}) failed: {ex.Message}"); }
            return null;
        }

        private static void DumpObjectCapabilities(object target, string tag)
        {
            try
            {
                var t = target.GetType();
                DriveLog($"[{tag}] type={t.FullName} asm={t.Assembly.GetName().Name}");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name == "GetService"))
                {
                    var ps = m.GetParameters();
                    var desc = m.IsGenericMethodDefinition ? $"generic(p={ps.Length})" : $"nongeneric({string.Join(",", ps.Select(p => p.ParameterType.Name))})";
                    DriveLog($"[{tag}] GetService: {desc}");
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (p.PropertyType.Name.IndexOf("Context", StringComparison.OrdinalIgnoreCase) >= 0 || p.PropertyType.Name.IndexOf("Working", StringComparison.OrdinalIgnoreCase) >= 0)
                        DriveLog($"[{tag}] PROP {p.PropertyType.FullName} {p.Name}");
                }
                foreach (var iface in t.GetInterfaces())
                    foreach (var p in iface.GetProperties())
                        if (p.PropertyType.Name.IndexOf("Context", StringComparison.OrdinalIgnoreCase) >= 0 || p.PropertyType.Name.IndexOf("Working", StringComparison.OrdinalIgnoreCase) >= 0)
                            DriveLog($"[{tag}] IFACE {iface.FullName} PROP {p.PropertyType.FullName} {p.Name}");
            }
            catch (Exception ex) { DriveLog($"[{tag}] dump failed: {ex.Message}"); }
        }

        private static object? GetWorkingContext(object? target)
        {
            if (target == null) return null;
            // log all interfaces of the target (diagnostic: does it implement ICoreObject / IWorkingContextProvider?)
            try
            {
                var ifaces = string.Join(",", target.GetType().GetInterfaces().Select(i => i.FullName));
                DriveLog($"[WC] target interfaces: {ifaces}");
            }
            catch (Exception ex) { DriveLog($"[WC] interface dump failed: {ex.Message}"); }

            var iwcType = ResolveType("Siemens.Automation.Basics.IWorkingContext");
            DriveLog($"[WC] iwcType = {(iwcType == null ? "null" : iwcType.FullName)}");

            // 1) System.IServiceProvider.GetService(typeof(IWorkingContext)) — standard service locator,
            //    bypasses the generic GetService<T>() constraint that rejects the *interface* type.
            if (iwcType != null && target is System.IServiceProvider sp)
            {
                try
                {
                    var r = sp.GetService(iwcType);
                    if (r != null) { DriveLog("wc via IServiceProvider.GetService(IWorkingContext) = ok"); return r; }
                    DriveLog("wc via IServiceProvider.GetService(IWorkingContext) = null");
                }
                catch (Exception ex) { DriveLog($"IServiceProvider.GetService(IWorkingContext) failed: {ex.Message}"); }
            }
            else
            {
                DriveLog($"IServiceProvider(IWorkingContext) route skipped: iwcType={(iwcType?.FullName ?? "null")}, targetIsSP={(target is System.IServiceProvider)}");
            }
            // 1b) also try System.IServiceProvider.GetService(typeof(ICoreObject)) — core object may be
            //     the real seed for the working context.
            var icoType = ResolveType("Siemens.Automation.ObjectFrame.ICoreObject");
            DriveLog($"[WC] icoType = {(icoType == null ? "null" : icoType.FullName)}");
            if (icoType != null && target is System.IServiceProvider sp2)
            {
                try
                {
                    var r = sp2.GetService(icoType);
                    if (r != null) DriveLog("ico via IServiceProvider.GetService(ICoreObject) = ok");
                    else DriveLog("ico via IServiceProvider.GetService(ICoreObject) = null");
                }
                catch (Exception ex) { DriveLog($"IServiceProvider.GetService(ICoreObject) failed: {ex.Message}"); }
            }
            // 2) IEngineeringServiceProvider.GetService(Type) (reflection, non-generic)
            var iespType = SafeType("Siemens.Engineering.IEngineeringServiceProvider");
            if (iespType != null && iwcType != null)
            {
                var m = iespType.GetMethods().FirstOrDefault(x => x.Name == "GetService" && !x.IsGenericMethodDefinition && x.GetParameters().Length == 1);
                if (m != null) { try { var r = m.Invoke(target, new object[] { iwcType }); if (r != null) { DriveLog("wc via IEngineeringServiceProvider.GetService = ok"); return r; } } catch (Exception ex) { DriveLog($"IEngineeringServiceProvider.GetService failed: {ex.Message}"); } }
            }
            // 3) direct public WorkingContext property
            var direct = GetProp(target, "WorkingContext");
            if (direct != null) return direct;
            // 4) IWorkingContextProvider interface (role types; unlikely on di/proj but covered)
            var wcp = target.GetType().GetInterfaces()
                .FirstOrDefault(i => i.FullName == "Siemens.Automation.Basics.IWorkingContextProvider");
            if (wcp != null)
            {
                var wp = wcp.GetProperty("WorkingContext");
                if (wp != null) { try { return wp.GetValue(target); } catch (Exception ex) { DriveLog($"WorkingContext getter failed: {ex.Message}"); } }
            }
            // 5) concrete implementer of IWorkingContext via generic GetService<T>() (satisfies the
            // constraint that the IWorkingContext *interface* violates)
            if (iwcType != null)
            {
                var c = GetWorkingContextViaConcrete(iwcType, target);
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>Request IWorkingContext by asking GetService&lt;T&gt;() for each concrete (non-abstract)
        /// class that implements IWorkingContext. The generic GetService&lt;T&gt;() constraint is satisfied by
        /// a concrete class even though it is violated by the interface itself.</summary>
        private static object? GetWorkingContextViaConcrete(Type iwcType, object target)
        {
            try
            {
                var gsp = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (gsp == null) return null;
                foreach (var t in iwcType.Assembly.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!iwcType.IsAssignableFrom(t)) continue;
                    try { var r = gsp.MakeGenericMethod(t).Invoke(target, null); if (r != null) { DriveLog($"wc via concrete {t.FullName} = ok"); return r; } }
                    catch (Exception ex) { DriveLog($"GetService<{t.Name}> failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { DriveLog($"GetWorkingContextViaConcrete failed: {ex.Message}"); }
            return null;
        }

        /// <summary>Comprehensive service-locator probe: for a target object, try IServiceProvider.GetService(...)
        /// for every candidate Acx/Common service type, and reflect the exact GetService&lt;T&gt; generic
        /// constraint. This maps where (if anywhere) the working context / role provider is reachable.</summary>
        private static void ProbeServices(object target, string tag)
        {
            if (target == null) return;
            try
            {
                DriveLog($"[PROBE:{tag}] type={target.GetType().FullName}");
                var sp = target as System.IServiceProvider;
                var svcNames = new[]
                {
                    "Siemens.Automation.Basics.IWorkingContext",
                    "Siemens.Automation.Basics.IWorkingContextProvider",
                    "Siemens.Automation.ObjectFrame.ICoreObject",
                    "Siemens.Automation.Basics.IDlc",
                    "Siemens.MC.Drives.Common.Openness.DLCs.RoleProviderDlc",
                    "Siemens.MC.Drives.Acx.Openness.DLCs.RoleProviderDlc",
                    "Siemens.MC.Drives.Acx.Openness.Interfaces.IStaticAccessHelper",
                    "Siemens.MC.Drives.Acx.Openness.RoleFactory",
                };
                foreach (var sn in svcNames)
                {
                    var st = ResolveType(sn);
                    if (st == null) { DriveLog($"[PROBE:{tag}] {sn} -> TYPE NOT RESOLVED"); continue; }
                    if (sp != null)
                    {
                        try
                        {
                            var r = sp.GetService(st);
                            DriveLog($"[PROBE:{tag}] IServiceProvider.GetService({sn}) = {(r == null ? "null" : "OK(" + r.GetType().FullName + ")")}");
                        }
                        catch (Exception ex) { DriveLog($"[PROBE:{tag}] IServiceProvider.GetService({sn}) EX: {ex.Message}"); }
                    }
                    else
                    {
                        DriveLog($"[PROBE:{tag}] IServiceProvider.GetService({sn}) = target not IServiceProvider");
                    }
                }
                // reflect the exact generic constraint on GetService<T>()
                var gsp = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (gsp != null)
                {
                    var gp = gsp.GetGenericArguments()[0];
                    var bases = string.Join(",", gp.GetGenericParameterConstraints().Select(c => c.FullName ?? c.Name));
                    DriveLog($"[PROBE:{tag}] GetService<T> gpAttrs={(int)gp.GenericParameterAttributes} constraints=[{bases}]");
                }
                else
                {
                    DriveLog($"[PROBE:{tag}] no generic GetService<T>() found");
                }
            }
            catch (Exception ex) { DriveLog($"[PROBE:{tag}] failed: {ex.Message}"); }
        }

        /// <summary>Find the SINAMICS drive-object-container service type among loaded assemblies.</summary>
        private static Type? FindDriveContainerType()
        {
            var candidates = new[]
            {
                "Siemens.Engineering.MC.Drives.DriveObjectContainer",
                "Siemens.Engineering.MC.Drives.IDriveObjectContainer",
                "Siemens.Engineering.Sinamics.DriveObjectContainer",
                "Siemens.Engineering.Sinamics.IDriveObjectContainer",
            };
            foreach (var full in candidates)
            {
                var t = SafeType(full);
                if (t != null) return t;
            }
            // broader scan: any type named DriveObjectContainer / IDriveObjectContainer in an MC.Drives namespace
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { ts = rtle.Types.Where(x => x != null).ToArray(); } catch { continue; }
                foreach (var t in ts)
                {
                    if (t == null) continue;
                    if ((t.Name == "DriveObjectContainer" || t.Name == "IDriveObjectContainer")
                        && (t.Namespace ?? "").IndexOf("MC.Drives", StringComparison.OrdinalIgnoreCase) >= 0)
                        return t;
                }
            }
            return null;
        }

        private static object? GetProp(object target, string name)
        {
            try
            {
                var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(target);
            }
            catch { }
            return null;
        }

        private static IEnumerable<object>? GetEnumerableProp(object target, params string[] names)
        {
            foreach (var n in names)
            {
                var v = GetProp(target, n);
                if (v is System.Collections.IEnumerable en)
                {
                    var list = new List<object>();
                    foreach (var e in en) if (e != null) list.Add(e);
                    if (list.Count > 0) return list;
                }
            }
            return null;
        }

        private static readonly string[] DriveObjectCollectionNames = new[] { "DriveObjects", "DriveObjectContainer", "Items" };
        private static readonly string[] ParameterCollectionNames = new[] { "Parameters", "AdvancedParameters", "Params" };
        private static readonly string[] TelegramCollectionNames = new[] { "Telegrams", "Telegram" };

        private static Type? IPropertyContainerType => SafeType("Siemens.Automation.Basics.DataBinding.IPropertyContainer");

        /// <summary>Get the settable property names of an IPropertyContainer (the keys accepted by Item[...]).</summary>
        private static List<string> PropertyContainerKeys(object target)
        {
            var keys = new List<string>();
            var pcType = IPropertyContainerType;
            if (pcType == null || !pcType.IsInstanceOfType(target)) return keys;
            var props = GetProp(target, "Properties") as System.Collections.IEnumerable;
            if (props != null)
            {
                foreach (var k in props) if (k is string s && !string.IsNullOrEmpty(s)) keys.Add(s);
            }
            return keys;
        }

        /// <summary>Get the IPropertyAdapter for a named property, via IPropertyContainer.Item[name].</summary>
        private static object? GetPropertyAdapter(object target, string propertyName)
        {
            var pcType = IPropertyContainerType;
            if (pcType == null || !pcType.IsInstanceOfType(target)) return null;
            var item = pcType.GetProperty("Item");
            if (item == null || item.GetIndexParameters().Length != 1) return null;
            try { return item.GetValue(target, new object[] { propertyName }); }
            catch (Exception ex) { DriveLog($"GetPropertyAdapter('{propertyName}') failed: {ex.Message}"); return null; }
        }

        private static object ConvertToPropertyType(object adapter, string valueStr)
        {
            try
            {
                var ptProp = adapter.GetType().GetProperty("PropertyType");
                var pt = ptProp?.GetValue(adapter) as Type;
                if (pt == null || pt == typeof(string)) return valueStr;
                try { return Convert.ChangeType(valueStr, pt); }
                catch { return valueStr; }
            }
            catch { return valueStr; }
        }

        private static string? ReadAdapterValue(object adapter)
        {
            try { return adapter.GetType().GetProperty("PropertyValue")?.GetValue(adapter)?.ToString(); }
            catch { return null; }
        }

        /// <summary>Set a property value on an IPropertyAdapter (PropertyValue setter, fallback SetValueWithoutCommand).</summary>
        private static void SetAdapterValue(object adapter, string valueStr)
        {
            var converted = ConvertToPropertyType(adapter, valueStr);
            var at = adapter.GetType();
            var pv = at.GetProperty("PropertyValue");
            if (pv != null && pv.CanWrite)
            {
                try { pv.SetValue(adapter, converted); return; }
                catch (Exception ex) { DriveLog($"PropertyValue set failed, trying SetValueWithoutCommand: {ex.Message}"); }
            }
            var sv = at.GetMethod("SetValueWithoutCommand", new[] { typeof(object) });
            if (sv != null) sv.Invoke(adapter, new object[] { converted });
            else throw new McpException("Cannot set value: neither PropertyValue setter nor SetValueWithoutCommand is available.");
        }

        private static string Identify(object browsable)
        {
            var id = GetProp(browsable, "IdentifierValue") as string;
            if (!string.IsNullOrEmpty(id)) return id;
            var nm = GetProp(browsable, "Name") as string;
            if (!string.IsNullOrEmpty(nm)) return nm;
            return browsable.GetType().Name;
        }

        // ---- discovery ----

        [McpServerTool(Name = "ListDriveModel"), Description("[L3][Drive/Startdrive] Enumerate the SINAMICS drive component tree of a device item added via Startdrive using the PUBLIC Openness HW API (no Startdrive Openness license required). Returns each child module (motor / measuring system / encoder / axis application) with its TypeName, OrderNumber, TypeIdentifier, PositionNumber and the full settable attribute list (e.g. Name, Comment). Use the output to learn exact module paths and writable attributes before calling SetDriveParameter. Requires an open TIA project.")]
        public static ResponseJsonReport ListDriveModel(
            [System.ComponentModel.Description("deviceItemPath: SINAMICS application device item path, e.g. 'SINAMICS S_1/驱动闭环控制' (the drive-application container that holds motor/encoder modules)")] string deviceItemPath)
        {
            DriveLog("=== ListDriveModel(HW) START ===");
            RequireConnected();
            var data = new JsonObject();
            try
            {
                var di = Portal.GetDeviceItem(deviceItemPath);
                if (di == null) throw new McpException($"Device item not found: {deviceItemPath}");
                DriveLog("device item resolved");

                var modules = new JsonArray();
                WalkHwModules(di, deviceItemPath, modules, 0, 3);

                data["deviceItemPath"] = deviceItemPath;
                data["moduleCount"] = modules.Count;
                data["modules"] = modules;
                data["note"] = "HW public-API drive model. Startdrive P-parameters (e.g. P0840) and telegrams are NOT exposed on this layer - they require the Startdrive Openness public API (Siemens.Engineering.Sinamics.dll).";
                DriveLog($"=== ListDriveModel(HW) OK ({modules.Count} modules) ===");
                return new ResponseJsonReport
                {
                    Ok = true,
                    Data = data,
                    Message = $"Drive HW model for '{deviceItemPath}' ({modules.Count} module(s))",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                data["error"] = ex.Message;
                DriveLog("EXCEPTION: " + ex.Message);
                return new ResponseJsonReport { Ok = false, Data = data, Message = $"partial: {ex.Message}", Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false } };
            }
        }

        /// <summary>Recursively walk DeviceItem.DeviceItems and emit module info (attributes + children).</summary>
        private static void WalkHwModules(DeviceItem node, string nodePath, JsonArray sink, int depth, int maxDepth)
        {
            try
            {
                var attrs = Helper.GetAttributeList(node);
                var attrArr = new JsonArray();
                var writable = new JsonArray();
                foreach (var a in attrs)
                {
                    attrArr.Add(new JsonObject { ["name"] = a.Name, ["value"] = a.Value?.ToString() ?? "", ["access"] = a.AccessMode ?? "" });
                    if (string.Equals(a.AccessMode, "ReadWrite", StringComparison.OrdinalIgnoreCase))
                        writable.Add(a.Name);
                }
                var entry = new JsonObject
                {
                    ["path"] = nodePath,
                    ["name"] = node.Name,
                    ["type"] = node.GetType().Name,
                    ["typeName"] = GetAttr(attrs, "TypeName"),
                    ["orderNumber"] = GetAttr(attrs, "OrderNumber"),
                    ["typeIdentifier"] = GetAttr(attrs, "TypeIdentifier"),
                    ["positionNumber"] = GetAttr(attrs, "PositionNumber"),
                    ["writableAttributes"] = writable,
                    ["attributes"] = attrArr
                };
                sink.Add(entry);
                if (depth >= maxDepth) return;
                if (node.DeviceItems != null)
                {
                    foreach (var child in node.DeviceItems)
                    {
                        if (child == null) continue;
                        WalkHwModules(child, nodePath + "/" + child.Name, sink, depth + 1, maxDepth);
                    }
                }
            }
            catch (Exception ex) { DriveLog($"WalkHwModules({nodePath}) failed: {ex.Message}"); }
        }

        private static string GetAttr(List<ModelContextProtocol.Attribute> attrs, string name)
        {
            var a = attrs.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return a?.Value?.ToString() ?? "";
        }

        // ---- writes ----

        [McpServerTool(Name = "SetDriveParameter"), Description("[L3][Drive/Startdrive] Set a writable module attribute (e.g. Name, Comment) on a SINAMICS drive component resolved via the PUBLIC Openness HW API. deviceItemPath must be a module path returned by ListDriveModel/GetDeviceItemTree, e.g. 'SINAMICS S_1/驱动闭环控制/电机_1'. parameter = the attribute name from ListDriveModel writableAttributes. NOTE: Startdrive P-parameters (P0840 etc.) and telegrams are NOT reachable on this HW layer - they require the Startdrive Openness public API. Requires an open TIA project.")]
        public static ResponseJsonReport SetDriveParameter(
            [System.ComponentModel.Description("deviceItemPath: module path resolved from ListDriveModel/GetDeviceItemTree, e.g. 'SINAMICS S_1/驱动闭环控制/电机_1'")] string deviceItemPath,
            [System.ComponentModel.Description("driveObject: ignored on the HW layer; kept for signature compatibility. Use deviceItemPath to address the module directly.")] string driveObject,
            [System.ComponentModel.Description("parameter: the writable attribute name from ListDriveModel (e.g. 'Comment', 'Name').")] string parameter,
            [System.ComponentModel.Description("property: ignored on the HW layer; kept for signature compatibility.")] string property = "Value",
            [System.ComponentModel.Description("value: the new attribute value as string (converted to the attribute's native type automatically).")] string value = "")
        {
            DriveLog("=== SetDriveParameter(HW) START ===");
            RequireConnected();
            var data = new JsonObject();
            try
            {
                if (string.IsNullOrEmpty(parameter)) throw new McpException("parameter (attribute name) is required, e.g. 'Comment'.");
                var result = Portal.SetDeviceItemAttribute(deviceItemPath, parameter, value);
                var meta = result.Meta ?? new JsonObject();
                data["deviceItemPath"] = deviceItemPath;
                data["attribute"] = parameter;
                data["before"] = meta["oldValue"]?.ToString() ?? "";
                data["after"] = meta["newValue"]?.ToString() ?? "";
                data["writable"] = meta["attributeWritable"]?.ToString() ?? "";
                data["error"] = meta["error"]?.ToString();
                var ok = meta["success"]?.GetValue<bool>() ?? false;
                DriveLog($"SetDriveParameter {deviceItemPath}.{parameter} -> {value} ok={ok}");
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Data = data,
                    Message = result.Message ?? (ok ? $"Set {parameter} = '{value}'" : "set failed"),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                data["error"] = ex.Message;
                DriveLog("EXCEPTION: " + ex.Message);
                return new ResponseJsonReport { Ok = false, Data = data, Message = $"failed: {ex.Message}", Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false } };
            }
        }

        [McpServerTool(Name = "SetDriveTelegram"), Description("[L3][Drive/Startdrive] Telegram (报文) configuration is NOT supported by the public Openness HW API on this machine - it requires the Startdrive Openness public API (Siemens.Engineering.Sinamics.dll) which is not installed. This tool always returns a clear unsupported error. Use ListDriveModel + SetDriveParameter for module attributes, or install Startdrive Openness for telegram/P-parameter support.")]
        public static ResponseJsonReport SetDriveTelegram(
            [System.ComponentModel.Description("deviceItemPath: SINAMICS application device item path.")] string deviceItemPath,
            [System.ComponentModel.Description("driveObject: drive object target (unused).")] string driveObject,
            [System.ComponentModel.Description("telegram: telegram identifier (unused).")] string telegram,
            [System.ComponentModel.Description("property: attribute name (unused).")] string property = "Value",
            [System.ComponentModel.Description("value: value (unused).")] string value = "")
        {
            RequireConnected();
            var data = new JsonObject
            {
                ["deviceItemPath"] = deviceItemPath,
                ["telegram"] = telegram,
                ["note"] = "Telegram configuration requires Startdrive Openness public API (Siemens.Engineering.Sinamics.dll), which is not installed on this machine. HW public-API layer cannot configure telegrams."
            };
            return new ResponseJsonReport
            {
                Ok = false,
                Data = data,
                Message = "Unsupported on this install: telegram configuration needs the Startdrive Openness public API (path B).",
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
            };
        }

        [McpServerTool(Name = "AddDriveComponent"), Description("[L3][Drive/Startdrive] Add a drive hardware component under a SINAMICS device item via the public Openness DeviceItem.PlugNew. Requires an exact, catalog-resolvable typeIdentifier - the OrderNumber shown by GetDeviceItemInfo (e.g. 'OrderNumber:1FK2102-1AG1x-xMxx' with wildcards) is a family placeholder and usually NOT pluggable; obtain a concrete identifier from the hardware catalog or an existing module's TypeIdentifierNormalized. NOTE: compact single-axis drives like S210 have a fixed topology (one motor / one encoder DRIVE-CLiQ port) - PlugNew for a second motor fails with 'Could not create the device item at the container'. Component addition is mainly meaningful on expandable drive units (S120/G120 etc.).")]
        public static ResponseMessage AddDriveComponent(
            [System.ComponentModel.Description("deviceItemPath: SINAMICS application device item path, e.g. 'SINAMICS S_1/驱动闭环控制'")] string deviceItemPath,
            [System.ComponentModel.Description("typeIdentifier: exact component type identifier / MLFB, e.g. 'DriveUnit' or a catalog MLFB")] string typeIdentifier,
            [System.ComponentModel.Description("name: name for the new component")] string name,
            [System.ComponentModel.Description("positionNumber: plug position (1-based); 0 lets TIA auto-place")] int positionNumber = 0)
        {
            try
            {
                RequireConnected();
                var di = Portal.GetDeviceItem(deviceItemPath);
                if (di == null) throw new McpException($"Device item not found: {deviceItemPath}");
                var plugNew = typeof(DeviceItem).GetMethods()
                    .FirstOrDefault(m => m.Name == "PlugNew" && m.GetParameters().Length == 3);
                if (plugNew == null) throw new McpException("PlugNew(string,string,int) not found on DeviceItem");
                var newItem = (DeviceItem)plugNew.Invoke(di, new object[] { typeIdentifier, name, positionNumber });
                return new ResponseMessage
                {
                    Message = $"Component '{name}' added under '{deviceItemPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["name"] = newItem?.Name }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                var chain = new System.Text.StringBuilder();
                Exception? e = ex;
                while (e != null) { chain.Append(e.Message).Append(" <= "); e = e.InnerException; }
                throw McpError.WithRecovery(ex, $"AddDriveComponent failed: {chain}");
            }
        }

        #endregion
    }
}
