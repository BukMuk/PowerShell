/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Microsoft.PowerShell.Commands;
using Dbg = System.Management.Automation.Diagnostics;

namespace System.Management.Automation
{
    /// <summary>
    /// Class describing a PowerShell module...
    /// </summary>
    public sealed class PSModuleInfo
    {
        internal const string DynamicModulePrefixString = "__DynamicModule_";

        private static readonly ReadOnlyDictionary<string, TypeDefinitionAst> EmptyTypeDefinitionDictionary = 
            new ReadOnlyDictionary<string, TypeDefinitionAst>(new Dictionary<string, TypeDefinitionAst>(StringComparer.OrdinalIgnoreCase));

        // This dictionary doesn't include ExportedTypes from nested modules.
        private ReadOnlyDictionary<string, TypeDefinitionAst> _exportedTypeDefinitionsNoNested { set; get; }

        private static readonly HashSet<string> ScriptModuleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            {   
                StringLiterals.PowerShellModuleFileExtension, 
            };

        internal static void SetDefaultDynamicNameAndPath(PSModuleInfo module)
        {
            string gs = Guid.NewGuid().ToString();
            module._path = gs;
            module._name = "__DynamicModule_" + gs;
        }

        /// <summary>
        /// This object describes a PowerShell module...
        /// </summary>
        /// <param name="path">The absolute path to the module</param>
        /// <param name="context">The execution context for this engine instance</param>
        /// <param name="sessionState">The module's sessionstate object - this may be null if the module is a dll.</param>
        internal PSModuleInfo(string path, ExecutionContext context, SessionState sessionState)
            : this(null, path, context, sessionState)
        {
        }

        /// <summary>
        /// This object describes a PowerShell module...
        /// </summary>
        /// <param name="name">The name to use for the module. If null, get it from the path name</param>
        /// <param name="path">The absolute path to the module</param>
        /// <param name="context">The execution context for this engine instance</param>
        /// <param name="sessionState">The module's sessionstate object - this may be null if the module is a dll.</param>
        internal PSModuleInfo(string name, string path, ExecutionContext context, SessionState sessionState)
        {
            if (path != null)
            {
                string resolvedPath = ModuleCmdletBase.GetResolvedPath(path, context);
                // The resolved path might be null if we're building a dynamic module and the path
                // is just a GUID, not an actual path that can be resolved.
                _path = resolvedPath ?? path;
            }

            _sessionState = sessionState;
            if (sessionState != null)
            {
                sessionState.Internal.Module = this;
            }

            // Use the name of basename of the path as the module name if no module name is supplied.
            if (name == null)
            {
                _name = ModuleIntrinsics.GetModuleName(_path);
            }
            else
            {
                _name = name;
            }
        }

        /// <summary>
        /// Default constructor to create an empty module info.
        /// </summary>
        public PSModuleInfo(bool linkToGlobal)
            : this(LocalPipeline.GetExecutionContextFromTLS(), linkToGlobal)
        {
        }

        /// <summary>
        /// Default constructor to create an empty module info.
        /// </summary>
        internal PSModuleInfo(ExecutionContext context, bool linkToGlobal)
        {
            if (context == null)
                throw new InvalidOperationException("PSModuleInfo");

            SetDefaultDynamicNameAndPath(this);

            // By default the top-level scope in a session state object is the global scope for the instance.
            // For modules, we need to set its global scope to be another scope object and, chain the top
            // level scope for this sessionstate instance to be the parent. The top level scope for this ss is the
            // script scope for the ss.

            // Allocate the session state instance for this module.
            _sessionState = new SessionState(context, true, linkToGlobal);
            _sessionState.Internal.Module = this;
        }

        /// <summary>
        /// Construct a PSModuleInfo instance initializing it from a scriptblock instead of a script file.
        /// </summary>
        /// <param name="scriptBlock">The scriptblock to use to initialize the module.</param>
        public PSModuleInfo(ScriptBlock scriptBlock)
        {
            if (scriptBlock == null)
            {
                throw PSTraceSource.NewArgumentException("scriptBlock");
            }

            // Get the ExecutionContext from the thread.
            var context = LocalPipeline.GetExecutionContextFromTLS();

            if (context == null)
                throw new InvalidOperationException("PSModuleInfo");

            SetDefaultDynamicNameAndPath(this);

            // By default the top-level scope in a session state object is the global scope for the instance.
            // For modules, we need to set its global scope to be another scope object and, chain the top
            // level scope for this sessionstate instance to be the parent. The top level scope for this ss is the
            // script scope for the ss.

            // Allocate the session state instance for this module.
            _sessionState = new SessionState(context, true, true);
            _sessionState.Internal.Module = this;

            // Now set up the module's session state to be the current session state
            SessionStateInternal oldSessionState = context.EngineSessionState;
            try
            {
                context.EngineSessionState = _sessionState.Internal;

                // Set the PSScriptRoot variable...
                context.SetVariable(SpecialVariables.PSScriptRootVarPath, _path);

                scriptBlock = scriptBlock.Clone();
                scriptBlock.SessionState = _sessionState;

                Pipe outputPipe = new Pipe { NullPipe = true };
                // And run the scriptblock...
                scriptBlock.InvokeWithPipe(
                    useLocalScope:         false,
                    errorHandlingBehavior: ScriptBlock.ErrorHandlingBehavior.WriteToCurrentErrorPipe,
                    dollarUnder:           AutomationNull.Value,
                    input:                 AutomationNull.Value,
                    scriptThis:            AutomationNull.Value,
                    outputPipe:            outputPipe,
                    invocationInfo:        null
                    );
            }
            finally
            {
                context.EngineSessionState = oldSessionState;
            }
        }

        internal bool ModuleHasPrivateMembers
        {
            get { return _moduleHasPrivateMembers; }
            set { _moduleHasPrivateMembers = value; }
        }

        private bool _moduleHasPrivateMembers;

        /// <summary>
        /// True if the module had errors during loading
        /// </summary>
        internal bool HadErrorsLoading { get; set; }

        /// <summary>
        /// ToString() implementation which returns the name of the module.
        /// </summary>
        /// <returns>The name of the module</returns>
        public override string ToString()
        {
            return this.Name;
        }

        private bool _logPipelineExecutionDetails = false;

        /// <summary> 
        /// Get/set whether to log Pipeline Execution Detail events. 
        /// </summary>
        public bool LogPipelineExecutionDetails
        {
            get
            {
                return _logPipelineExecutionDetails;
            }
            set
            {
                _logPipelineExecutionDetails = value;
            }
        }

        /// <summary>
        /// The name of this module.
        /// </summary>
        public string Name
        {
            get
            {
                return _name;
            }
        }
        /// <summary>
        /// Sets the name property of the PSModuleInfo object
        /// </summary>
        /// <param name="name">The name to set it to</param>
        internal void SetName(string name)
        {
            _name = name;
        }
        string _name = String.Empty;

        /// <summary>
        /// The path to the file that defined this module...
        /// </summary>
        public string Path
        {
            get
            {
                return _path;
            }
            internal set
            {
                _path = value;
            }
        }
        string _path = String.Empty;

        /// <summary>
        /// If the module is a binary module or a script module that defines
        /// classes, this property if a reference to the assembly, otherwise
        /// it is null.
        /// </summary>
        public Assembly ImplementingAssembly { get; internal set; }

        /// <summary>
        /// If this is a script module, then this property will contain
        /// the PowerShell source text that was used to define this module.
        /// </summary>
        public string Definition
        {
            get { return _definitionExtent == null ? String.Empty : _definitionExtent.Text; }
        }
        internal IScriptExtent _definitionExtent;

        /// <summary>
        /// A description of this module...
        /// </summary>
        public string Description
        {
            get { return _description; }
            set { _description = value ?? String.Empty; }
        }
        string _description = String.Empty;

        /// <summary>
        /// The guid for this module if one was defined in the module manifest.
        /// </summary>
        public Guid Guid
        {
            get { return _guid; }
        }

        internal void SetGuid(Guid guid)
        {
            _guid = guid;
        }
        Guid _guid;

        /// <summary>
        /// The HelpInfo for this module if one was defined in the module manifest.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1056:UriPropertiesShouldNotBeStrings")]
        public string HelpInfoUri
        {
            get { return _helpInfoUri; }
        }

        internal void SetHelpInfoUri(string uri)
        {
            _helpInfoUri = uri;
        }
        string _helpInfoUri;

        /// <summary>
        /// Get the module base directory for this module. For modules loaded via a module
        /// manifest, this will be the directory containting the manifest file rather than
        /// the directory containing the actual module file. This is particularly useful
        /// when loading a GAC'ed assembly.
        /// </summary>
        public string ModuleBase
        {
            get
            {
                return _moduleBase ??
                       (_moduleBase = !string.IsNullOrEmpty(_path) ? IO.Path.GetDirectoryName(_path) : string.Empty);
            }
        }
        internal void SetModuleBase(string moduleBase)
        {
            _moduleBase = moduleBase;
        }
        string _moduleBase;

        /// <summary>
        /// This value is set from the PrivateData member in the module manifest.
        /// It allows implementor specific data to be passed to the module
        /// via the manifest file.
        /// </summary>
        public object PrivateData
        {
            get
            {
                return _privateData;
            }

            set
            {
                _privateData = value;
                SetPSDataPropertiesFromPrivateData();
            }
        }
        private object _privateData = null;

        private void SetPSDataPropertiesFromPrivateData()
        {
            // Reset the old values of PSData properties.
            _tags.Clear();
            ReleaseNotes = null;
            LicenseUri = null;
            ProjectUri = null;
            IconUri = null;

            var privateDataHashTable = _privateData as Hashtable;
            if (privateDataHashTable != null)
            {
                var psData = privateDataHashTable["PSData"] as Hashtable;
                if (psData != null)
                {
                    object tagsValue = psData["Tags"];
                    if (tagsValue != null)
                    {
                        var tags = tagsValue as object[];
                        if (tags != null && tags.Any())
                        {
                            foreach (var tagString in tags.OfType<string>())
                            {
                                AddToTags(tagString);
                            }
                        }
                        else
                        {
                            AddToTags(tagsValue.ToString());
                        }
                    }

                    var licenseUri = psData["LicenseUri"] as string;
                    if (licenseUri != null)
                    {
                        LicenseUri = GetUriFromString(licenseUri);
                    }

                    var projectUri = psData["ProjectUri"] as string;
                    if (projectUri != null)
                    {
                        ProjectUri = GetUriFromString(projectUri);
                    }

                    var iconUri = psData["IconUri"] as string;
                    if (iconUri != null)
                    {
                        IconUri = GetUriFromString(iconUri);
                    }

                    ReleaseNotes = psData["ReleaseNotes"] as string;
                }
            }
        }

        private static Uri GetUriFromString(string uriString)
        {
            Uri uri = null;
            if (uriString != null)
            {
                // try creating the Uri object
                // Ignoring the return value from Uri.TryCreate(), as uri value will be null on false or valid uri object on true.
                Uri.TryCreate(uriString, UriKind.Absolute, out uri);
            }

            return uri;
        }

        /// <summary>
        /// Tags of this module.
        /// </summary>
        public IEnumerable<String> Tags
        {
            get { return _tags; }
        }

        private readonly List<string> _tags = new List<string>();

        internal void AddToTags(string tag)
        {
            _tags.Add(tag);
        }

        /// <summary>
        /// ProjectUri of this module.
        /// </summary>
        public Uri ProjectUri { get; internal set; }

        /// <summary>
        /// IconUri of this module.
        /// </summary>
        public Uri IconUri { get; internal set; }

        /// <summary>
        /// LicenseUri of this module.
        /// </summary>
        public Uri LicenseUri { get; internal set; }

        /// <summary>
        /// ReleaseNotes of this module.
        /// </summary>
        public string ReleaseNotes { get; internal set; }

        /// <summary>
        /// Repository SourceLocation of this module.
        /// </summary>
        public Uri RepositorySourceLocation { get; internal set; }

        /// <summary>
        /// The version of this module
        /// </summary>
        public Version Version
        {
            get { return _version; }
        }

        /// <summary>
        /// Sets the module version
        /// </summary>
        /// <param name="version">the version to set...</param>
        internal void SetVersion(Version version)
        {
            _version = version;
        }
        Version _version = new Version(0, 0);

        /// <summary>
        /// True if the module was compiled (i.e. a .DLL) instead of
        /// being in PowerShell script...
        /// </summary>
        public ModuleType ModuleType
        {
            get { return _moduleType; }
        }
        ModuleType _moduleType = ModuleType.Script;

        /// <summary>
        /// This this module as being a compiled module...
        /// </summary>
        internal void SetModuleType(ModuleType moduleType) { _moduleType = moduleType; }

        /// <summary>
        /// Module Author
        /// </summary>
        public string Author
        {
            get; internal set;
        }
        
        /// <summary>
        /// Controls the module access mode...
        /// </summary>
        public ModuleAccessMode AccessMode
        {
            get { return _accessMode; }
            set
            {
                if (_accessMode == ModuleAccessMode.Constant)
                {
                    throw PSTraceSource.NewInvalidOperationException();
                }
                _accessMode = value;
            }
        }
        ModuleAccessMode _accessMode = ModuleAccessMode.ReadWrite;
        
        /// <summary>
        /// CLR Version
        /// </summary>
        public Version ClrVersion
        {
            get;
            internal set;
        }

        /// <summary>
        /// Company Name
        /// </summary>
        public String CompanyName
        {
            get;
            internal set;
        }

        /// <summary>
        /// Copyright
        /// </summary>
        public String Copyright
        {
            get;
            internal set;
        }

        /// <summary>
        /// .NET Framework Version
        /// </summary>
        public Version DotNetFrameworkVersion
        {
            get;
            internal set;
        }

        /// <summary>
        /// Lists the functions exported by this module...
        /// </summary>
        public Dictionary<string, FunctionInfo> ExportedFunctions
        {
            get
            {
                Dictionary<string, FunctionInfo> exports = new Dictionary<string, FunctionInfo>(StringComparer.OrdinalIgnoreCase);

                // If the module is not binary, it may also have functions...
                if ((DeclaredFunctionExports != null) && (DeclaredFunctionExports.Count > 0))
                {
                    foreach (string fn in DeclaredFunctionExports)
                    {
                        FunctionInfo tempFunction = new FunctionInfo(fn, ScriptBlock.EmptyScriptBlock, null) {Module = this};
                        exports[fn] = tempFunction;
                    }
                }
                else if ((DeclaredFunctionExports != null) && (DeclaredFunctionExports.Count == 0))
                {
                    return exports;
                }
                else if (_sessionState != null)
                {
                    // If there is no session state object associated with this list, 
                    // just return a null list of exports...
                    if (_sessionState.Internal.ExportedFunctions != null)
                    {
                        foreach (FunctionInfo fi in _sessionState.Internal.ExportedFunctions)
                        {
                            if (!exports.ContainsKey(fi.Name))
                            {
                                exports[ModuleCmdletBase.AddPrefixToCommandName(fi.Name,fi.Prefix)] = fi;
                            }
                        }
                    }
                }
                else
                {
                    foreach (var detectedExport in _detectedFunctionExports)
                    {
                        if (!exports.ContainsKey(detectedExport))
                        {
                            FunctionInfo tempFunction = new FunctionInfo(detectedExport, ScriptBlock.EmptyScriptBlock, null) {Module = this};
                            exports[detectedExport] = tempFunction;
                        }
                    }
                }
                return exports;
            }
        }


        private bool IsScriptModuleFile(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            return ext != null && ScriptModuleExtensions.Contains(ext);
        }

        /// <summary>
        /// Lists the types (PowerShell classes, enums, interfaces) exported by this module.
        /// This returns ASTs for types, created in parse time.
        /// </summary>
        public ReadOnlyDictionary<string, TypeDefinitionAst> GetExportedTypeDefinitions()
        {
            // We cache exported types from this modules, but not from nestedModules, 
            // because we may not have NestedModules list populated on the first call.
            // TODO(sevoroby): it may harm perf a little bit. Can we sort it out?

            if (_exportedTypeDefinitionsNoNested == null)
            {
                string rootedPath = null;
                if (RootModule == null)
                {
                    if (this.Path != null)
                    {
                        rootedPath = this.Path;
                    }
                }
                else
                {
                    rootedPath = IO.Path.Combine(this.ModuleBase, this.RootModule);
                }

                // ExternalScriptInfo.GetScriptBlockAst() uses a cache layer to avoid re-parsing.
                CreateExportedTypeDefinitions(rootedPath != null && IsScriptModuleFile(rootedPath) && IO.File.Exists(rootedPath) ? 
                    (new ExternalScriptInfo(rootedPath, rootedPath)).GetScriptBlockAst() : null);
            }

            var res = new Dictionary<string, TypeDefinitionAst>(StringComparer.OrdinalIgnoreCase);
            if (this.NestedModules != null)
            {
                foreach (var nestedModule in this.NestedModules)
                {
                    if (nestedModule == this)
                    {
                        // this is totally bizzare, but it happens for some reasons for 
                        // Microsoft.Powershell.Workflow.ServiceCore.dll, when there is a workflow defined in a nested module.
                        // TODO(sevoroby): we should handle possible circular dependencies
                        continue;
                    }

                    foreach (var typePairs in nestedModule.GetExportedTypeDefinitions())
                    {
                        // The last one name wins! It's the same for command names in nested modules.
                        // For rootModule C with Two nested modules (A, B) the order is: A, B, C
                        res[typePairs.Key] = typePairs.Value;
                    }
                }
                foreach (var typePairs in _exportedTypeDefinitionsNoNested)
                {
                    res[typePairs.Key] = typePairs.Value;
                }
            }

            return new ReadOnlyDictionary<string, TypeDefinitionAst>(res);
        }

        /// <summary>
        /// Create ExportedTypeDefinitions from ast.
        /// </summary>
        /// <param name="moduleContentScriptBlockAsts"></param>
        internal void CreateExportedTypeDefinitions(ScriptBlockAst moduleContentScriptBlockAsts)
        {
            if (moduleContentScriptBlockAsts == null)
            {
                this._exportedTypeDefinitionsNoNested = EmptyTypeDefinitionDictionary;
            }
            else
            {

                this._exportedTypeDefinitionsNoNested = new ReadOnlyDictionary<string, TypeDefinitionAst>(
                    moduleContentScriptBlockAsts.FindAll(a => (a is TypeDefinitionAst), false)
                        .OfType<TypeDefinitionAst>()
                        .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase));
            }
        }

        internal void AddDetectedTypeExports(List<TypeDefinitionAst> typeDefinitions)
        {
            this._exportedTypeDefinitionsNoNested = new ReadOnlyDictionary<string, TypeDefinitionAst>(
                typeDefinitions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Prefix
        /// </summary>
        public String Prefix
        {
            get;
            internal set;
        }

        internal Collection<string> DeclaredFunctionExports = null;
        internal List<string> _detectedFunctionExports = new List<string>();

        internal List<string> _detectedWorkflowExports = new List<string>();

        /// <summary>
        /// Add function to the fixed exports list
        /// </summary>
        /// <param name="name">the function to add</param>
        internal void AddDetectedFunctionExport(string name)
        {
            Dbg.Assert(name != null, "AddDetectedFunctionExport should not be called with a null value");

            if (!_detectedFunctionExports.Contains(name))
            {
                _detectedFunctionExports.Add(name);
            }
        }

        /// <summary>
        /// Add workflow to the fixed exports list
        /// </summary>
        /// <param name="name">the function to add</param>
        internal void AddDetectedWorkflowExport(string name)
        {
            Dbg.Assert(name != null, "AddDetectedWorkflowExport should not be called with a null value");

            if (!_detectedWorkflowExports.Contains(name))
            {
                _detectedWorkflowExports.Add(name);
            }
        }

        /// <summary>
        /// Lists the functions exported by this module...
        /// </summary>
        public Dictionary<string, CmdletInfo> ExportedCmdlets
        {
            get
            {
                Dictionary<string, CmdletInfo> exports = new Dictionary<string, CmdletInfo>(StringComparer.OrdinalIgnoreCase);

                if ((DeclaredCmdletExports != null) && (DeclaredCmdletExports.Count > 0))
                {
                    foreach (string fn in DeclaredCmdletExports)
                    {
                        CmdletInfo tempCmdlet = new CmdletInfo(fn, null, null, null, null) {Module = this};
                        exports[fn] = tempCmdlet;
                    }
                }
                else if ((DeclaredCmdletExports != null) && (DeclaredCmdletExports.Count == 0))
                {
                    return exports;
                }
                else if ((CompiledExports != null) && (CompiledExports.Count > 0))
                {
                    foreach (CmdletInfo cmdlet in CompiledExports)
                    {
                        exports[cmdlet.Name] = cmdlet;
                    }
                }
                else
                {
                    foreach (string detectedExport in _detectedCmdletExports)
                    {
                        if (!exports.ContainsKey(detectedExport))
                        {
                            CmdletInfo tempCmdlet = new CmdletInfo(detectedExport, null, null, null, null) {Module = this};
                            exports[detectedExport] = tempCmdlet;
                        }
                    }
                }

                return exports;
            }
        }
        internal Collection<string> DeclaredCmdletExports = null;
        internal List<string> _detectedCmdletExports = new List<string>();

        /// <summary>
        /// Add CmdletInfo to the fixed exports list...
        /// </summary>
        /// <param name="cmdlet">the cmdlet to add...</param>
        internal void AddDetectedCmdletExport(string cmdlet)
        {
            Dbg.Assert(cmdlet != null, "AddDetectedCmdletExport should not be called with a null value");

            if(! _detectedCmdletExports.Contains(cmdlet))
            {
                _detectedCmdletExports.Add(cmdlet);
            }
        }

        /// <summary>
        /// Gets the aggregated list of visible commands exported from the module. If there are two
        /// commands of different types exported with the same name (e.g. alias 'foo' and cmdlet 'foo') the
        /// combined dictionary will only contain the highest precidence cmdlet (e.g. the alias 'foo' since
        /// aliases shadow cmdlets.
        /// </summary>
        public Dictionary<string, CommandInfo> ExportedCommands
        {
            get
            {
                Dictionary<string, CommandInfo> exports = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, CmdletInfo> cmdlets = this.ExportedCmdlets;
                if (cmdlets != null)
                {
                    foreach (var cmdlet in cmdlets)
                    {
                        exports[cmdlet.Key] = cmdlet.Value;
                    }
                }

                Dictionary<string, FunctionInfo> functions = this.ExportedFunctions;
                if (functions != null)
                {
                    foreach (var function in functions)
                    {
                        exports[function.Key] = function.Value;
                    }
                }

                Dictionary<string, FunctionInfo> workflows = this.ExportedWorkflows;
                if (workflows != null)
                {
                    foreach (var workflow in workflows)
                    {
                        exports[workflow.Key] = workflow.Value;
                    }
                }

                Dictionary<string, AliasInfo> aliases = this.ExportedAliases;
                if (aliases != null)
                {
                    foreach (var alias in aliases)
                    {
                        exports[alias.Key] = alias.Value;
                    }
                }

                return exports;
            }
        }

        /// <summary>
        /// Add CmdletInfo to the fixed exports list...
        /// </summary>
        /// <param name="cmdlet">the cmdlet to add...</param>
        internal void AddExportedCmdlet(CmdletInfo cmdlet)
        {
            Dbg.Assert(cmdlet != null, "AddExportedCmdlet should not be called with a null value");
            _compiledExports.Add(cmdlet);
        }

        /// <summary>
        /// Return the merged list of exported cmdlets. This is necessary
        /// because you may have a binary module with nested modules where
        /// some cmdlets come from the module and others come from the nested
        /// module. We need to consolidate the list so it can properly be constrained.
        /// </summary>
        internal List<CmdletInfo> CompiledExports
        {
            get
            {
                // If this module has a session state instance and there are any
                // exported cmdlets in the session state, migrate them to the
                // module info _compiledCmdlets entry.
                if (_sessionState != null && _sessionState.Internal.ExportedCmdlets != null &&
                    _sessionState.Internal.ExportedCmdlets.Count > 0)
                {
                    foreach (CmdletInfo ci in _sessionState.Internal.ExportedCmdlets)
                    {
                        _compiledExports.Add(ci);
                    }
                    _sessionState.Internal.ExportedCmdlets.Clear();
                }
                return _compiledExports;
            }
        }

        readonly List<CmdletInfo> _compiledExports = new List<CmdletInfo>();


        /// <summary>
        /// Add AliasInfo to the fixed exports list...
        /// </summary>
        /// <param name="aliasInfo">the cmdlet to add...</param>
        internal void AddExportedAlias(AliasInfo aliasInfo)
        {
            Dbg.Assert(aliasInfo != null, "AddExportedAlias should not be called with a null value");
            _compiledAliasExports.Add(aliasInfo);
        }

        /// <summary>
        /// Return the merged list of exported aliases. This is necessary
        /// because you may have a binary module with nested modules where
        /// some aliases come from the module and others come from the nested
        /// module. We need to consolidate the list so it can properly be constrained.
        /// </summary>
        internal List<AliasInfo> CompiledAliasExports
        {
            get
            {
                return _compiledAliasExports;
            }
        }

        readonly List<AliasInfo> _compiledAliasExports = new List<AliasInfo>();


        /// <summary>
        /// FileList
        /// </summary>
        public IEnumerable<String> FileList
        {
            get { return _fileList; }
        }

        private List<string> _fileList = new List<string>();

        internal void AddToFileList(string file)
        {
            _fileList.Add(file);
        }

        /// <summary>
        /// CompatiblePSEditions
        /// </summary>
        public IEnumerable<String> CompatiblePSEditions
        {
            get { return _compatiblePSEditions; }
        }

        private List<string> _compatiblePSEditions = new List<string>();

        internal void AddToCompatiblePSEditions(string psEdition)
        {
            _compatiblePSEditions.Add(psEdition);
        }

        /// <summary>
        /// ModuleList
        /// </summary>
        public IEnumerable<object> ModuleList
        {
            get { return _moduleList; }
        }

        private Collection<object> _moduleList = new Collection<object>();

        internal void AddToModuleList(object m)
        {
            _moduleList.Add(m);
        }

        /// <summary>
        /// Returns the list of child modules of this module. This will only
        /// be non-empty for module manifests.
        /// </summary>
        public ReadOnlyCollection<PSModuleInfo> NestedModules
        {
            get
            {
                return _readonlyNestedModules ??
                       (_readonlyNestedModules = new ReadOnlyCollection<PSModuleInfo>(_nestedModules));
            }
        }
        ReadOnlyCollection<PSModuleInfo> _readonlyNestedModules;

        /// <summary>
        /// Add a module to the list of child modules.
        /// </summary>
        /// <param name="nestedModule">The module to add</param>
        internal void AddNestedModule(PSModuleInfo nestedModule)
        {
            AddModuleToList(nestedModule, _nestedModules);
        }
        readonly List<PSModuleInfo> _nestedModules = new List<PSModuleInfo>();

        /// <summary>
        /// PowerShell Host Name
        /// </summary>
        public String PowerShellHostName
        {
            get;
            internal set;
        }

        /// <summary>
        /// PowerShell Host Version
        /// </summary>
        public Version PowerShellHostVersion
        {
            get;
            internal set;
        }

        /// <summary>
        /// PowerShell Version
        /// </summary>
        public Version PowerShellVersion
        {
            get;
            internal set;
        }

        /// <summary>
        /// Processor Architecture
        /// </summary>
        public ProcessorArchitecture ProcessorArchitecture
        {
            get;
            internal set;
        }

        /// <summary>
        /// Scripts to Process
        /// </summary>
        public IEnumerable<String> Scripts
        { 
            get { return _scripts; }
        }

        private List<String> _scripts = new List<string>();

        internal void AddScript(string s)
        {
            _scripts.Add(s);
        }

        /// <summary>
        /// Required Assemblies
        /// </summary>
        public IEnumerable<String> RequiredAssemblies
        {
            get { return _requiredAssemblies; }
        }
        private Collection<String> _requiredAssemblies = new Collection<string>();

        internal void AddRequiredAssembly(string assembly)
        {
            _requiredAssemblies.Add(assembly);
        }

        /// <summary>
        /// Returns the list of required modules of this module. This will only
        /// be non-empty for module manifests.
        /// </summary>
        public ReadOnlyCollection<PSModuleInfo> RequiredModules
        {
            get
            {
                return _readonlyRequiredModules ??
                       (_readonlyRequiredModules = new ReadOnlyCollection<PSModuleInfo>(_requiredModules));
            }
        }
        ReadOnlyCollection<PSModuleInfo> _readonlyRequiredModules;

        /// <summary>
        /// Add a module to the list of required modules.
        /// </summary>
        /// <param name="requiredModule">The module to add</param>
        internal void AddRequiredModule(PSModuleInfo requiredModule)
        {
            AddModuleToList(requiredModule, _requiredModules);
        }
        List<PSModuleInfo> _requiredModules = new List<PSModuleInfo>();

        /// <summary>
        /// Returns the list of required modules specified in the module manifest of this module. This will only
        /// be non-empty for module manifests.
        /// </summary>
        internal ReadOnlyCollection<ModuleSpecification> RequiredModulesSpecification
        {
            get
            {
                return _readonlyRequiredModulesSpecification ??
                       (_readonlyRequiredModulesSpecification = new ReadOnlyCollection<ModuleSpecification>(_requiredModulesSpecification));
            }
        }
        ReadOnlyCollection<ModuleSpecification> _readonlyRequiredModulesSpecification;

        /// <summary>
        /// Add a module to the list of required modules specification
        /// </summary>
        /// <param name="requiredModuleSpecification">The module to add</param>
        internal void AddRequiredModuleSpecification(ModuleSpecification requiredModuleSpecification)
        {
            _requiredModulesSpecification.Add(requiredModuleSpecification);
        }
        List<ModuleSpecification> _requiredModulesSpecification = new List<ModuleSpecification>();

        /// <summary>
        /// Root Module
        /// </summary>
        public String RootModule
        {
            get;
            internal set;
        }

        /// <summary>
        /// This member is used to copy over the RootModule in case the module is a manifest module
        /// This is so that only ModuleInfo for modules with type=Manifest have RootModule populated
        /// </summary>
        internal String RootModuleForManifest
        {
            get; 
            set; 
        }

        /// <summary>
        /// Add a module to the list of modules, avoiding adding duplicates.
        /// </summary>
        private static void AddModuleToList(PSModuleInfo module, List<PSModuleInfo> moduleList)
        {
            Dbg.Assert(module != null, "AddModuleToList should not be called with a null value");
            // Add the module if it isn't already there...
            foreach (PSModuleInfo m in moduleList)
            {
                if (m.Path.Equals(module.Path, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            moduleList.Add(module);
        }

        internal static string[] _builtinVariables = new string[] { "_", "this", "input", "args", "true", "false", "null",
            "MaximumErrorCount", "MaximumVariableCount", "MaximumFunctionCount","MaximumAliasCount", "PSDefaultParameterValues",
            "MaximumDriveCount", "Error", "PSScriptRoot", "PSCommandPath", "MyInvocation", "ExecutionContext", "StackTrace" };

        /// <summary>
        /// Lists the variables exported by this module.
        /// </summary>
        public Dictionary<string, PSVariable> ExportedVariables
        {
            get
            {
                Dictionary<string, PSVariable> exportedVariables = new Dictionary<string, PSVariable>(StringComparer.OrdinalIgnoreCase);

                if ((DeclaredVariableExports != null) && (DeclaredVariableExports.Count > 0))
                {
                    foreach (string fn in DeclaredVariableExports)
                    {
                        exportedVariables[fn] = null;
                    }
                }
                else
                {
                    // If there is no session state object associated with this list, 
                    // just return a null list of exports. This will be true if the
                    // module is a compiled module.
                    if (_sessionState == null || _sessionState.Internal.ExportedVariables == null)
                    {
                        return exportedVariables;
                    }

                    foreach (PSVariable v in _sessionState.Internal.ExportedVariables)
                    {
                        exportedVariables[v.Name] = v;
                    }
                }

                return exportedVariables;
            }
        }
        internal Collection<string> DeclaredVariableExports = null;

        /// <summary>
        /// Lists the aliases exported by this module.
        /// </summary>
        public Dictionary<string, AliasInfo> ExportedAliases
        {
            get
            {
                Dictionary<string, AliasInfo> exportedAliases = new Dictionary<string, AliasInfo>(StringComparer.OrdinalIgnoreCase);

                if ((DeclaredAliasExports != null) && (DeclaredAliasExports.Count > 0))
                {
                    foreach (string fn in DeclaredAliasExports)
                    {
                        AliasInfo tempAlias = new AliasInfo(fn, null, null) {Module = this};
                        exportedAliases[fn] = tempAlias;
                    }
                }
                else if ((CompiledAliasExports != null) && (CompiledAliasExports.Count > 0))
                {
                    foreach (AliasInfo ai in CompiledAliasExports)
                    {
                        exportedAliases[ai.Name] = ai;
                    }
                }
                else
                {
                    // There is no session state object associated with this list.
                    if (_sessionState == null)
                    {
                        // Check if we detected any
                        if (_detectedAliasExports.Count > 0)
                        {
                            foreach (var pair in _detectedAliasExports)
                            {
                                string detectedExport = pair.Key;
                                if (! exportedAliases.ContainsKey(detectedExport))
                                {
                                    AliasInfo tempAlias = new AliasInfo(detectedExport, pair.Value, null) {Module = this};
                                    exportedAliases[detectedExport] = tempAlias;
                                }
                            }
                        }
                        else
                        {
                            // just return a null list of exports. This will be true if the
                            // module is a compiled module.
                            return exportedAliases;
                        }
                    }
                    else
                    {
                        // We have a session state
                        foreach (AliasInfo ai in _sessionState.Internal.ExportedAliases)
                        {
                            exportedAliases[ai.Name] = ai;
                        }
                    }
                }

                return exportedAliases;
            }
        }
        internal Collection<string> DeclaredAliasExports = null;
        internal Dictionary<string, string> _detectedAliasExports = new Dictionary<string, string>();

        /// <summary>
        /// Add alias to the detected alias list
        /// </summary>
        /// <param name="name">the alias to add</param>
        /// <param name="value">the command it resolves to</param>
        internal void AddDetectedAliasExport(string name, string value)
        {
            Dbg.Assert(name != null, "AddDetectedAliasExport should not be called with a null value");

            _detectedAliasExports[name] = value;
        }

        /// <summary>
        /// Lists the workflows exported by this module.
        /// </summary>
        public Dictionary<string, FunctionInfo> ExportedWorkflows
        {
            get
            {
                Dictionary<string, FunctionInfo> exportedWorkflows = new Dictionary<string, FunctionInfo>(StringComparer.OrdinalIgnoreCase);

                if ((DeclaredWorkflowExports != null) && (DeclaredWorkflowExports.Count > 0))
                {
                    foreach (string fn in DeclaredWorkflowExports)
                    {
                        WorkflowInfo tempWf = new WorkflowInfo(fn, ScriptBlock.EmptyScriptBlock, context: null) {Module = this};
                        exportedWorkflows[fn] = tempWf;
                    }
                }
                if ((DeclaredWorkflowExports != null) && (DeclaredWorkflowExports.Count == 0))
                {
                    return exportedWorkflows;
                }
                else
                {
                    // If there is no session state object associated with this list, 
                    // just return a null list of exports. This will be true if the
                    // module is a compiled module.
                    if (_sessionState == null)
                    {
                        foreach (string detectedExport in _detectedWorkflowExports)
                        {
                            if (!exportedWorkflows.ContainsKey(detectedExport))
                            {
                                WorkflowInfo tempWf = new WorkflowInfo(detectedExport, ScriptBlock.EmptyScriptBlock, context: null) {Module = this};
                                exportedWorkflows[detectedExport] = tempWf;
                            }
                        }
                        return exportedWorkflows;
                    }

                    foreach (WorkflowInfo wi in _sessionState.Internal.ExportedWorkflows)
                    {
                        exportedWorkflows[wi.Name] = wi;
                    }
                }

                return exportedWorkflows;
            }
        }
        internal Collection<string> DeclaredWorkflowExports = null;

        /// <summary>
        /// 
        /// </summary>
        public ReadOnlyCollection<string> ExportedDscResources
        {
            get
            {
                return _declaredDscResourceExports != null
                    ? new ReadOnlyCollection<string>(_declaredDscResourceExports)
                    : Utils.EmptyReadOnlyCollection<string>();
            }
        }
 
        internal Collection<string> _declaredDscResourceExports = null;
   
        /// <summary>
        /// The session state instance associated with this module.
        /// </summary>
        public SessionState SessionState
        {
            get
            {
                return _sessionState;
            }
            set
            {
                _sessionState = value;
            }
        }
        private SessionState _sessionState;

        /// <summary>
        /// Returns a new scriptblock bound to this module instance.
        /// </summary>
        /// <param name="scriptBlockToBind">The original scriptblock</param>
        /// <returns>The new bound scriptblock</returns>
        public ScriptBlock NewBoundScriptBlock(ScriptBlock scriptBlockToBind)
        {
            var context = LocalPipeline.GetExecutionContextFromTLS();
            return NewBoundScriptBlock(scriptBlockToBind, context);
        }

        internal ScriptBlock NewBoundScriptBlock(ScriptBlock scriptBlockToBind, ExecutionContext context)
        {
            if (_sessionState == null || context == null)
            {
                throw PSTraceSource.NewInvalidOperationException(Modules.InvalidOperationOnBinaryModule);
            }

            ScriptBlock newsb;

            // Now set up the module's session state to be the current session state
            lock (context.EngineSessionState)
            {
                SessionStateInternal oldSessionState = context.EngineSessionState;

                try
                {
                    context.EngineSessionState = _sessionState.Internal;
                    newsb = scriptBlockToBind.Clone();
                    newsb.SessionState = _sessionState;
                }
                finally
                {
                    context.EngineSessionState = oldSessionState;
                }
            }

            return newsb;
        }

        /// <summary>
        /// Invoke a scriptblock in the context of this module...
        /// </summary>
        /// <param name="sb">The scriptblock to invoke</param>
        /// <param name="args">Arguments to the scriptblock</param>
        /// <returns>The result of the invocation</returns>
        public object Invoke(ScriptBlock sb, params object[] args)
        {
            if (sb == null)
                return null;

            // Temporarily set the scriptblocks session state to be the
            // modules...
            SessionStateInternal oldSessionState = sb.SessionStateInternal;
            object result;
            try
            {
                sb.SessionStateInternal = _sessionState.Internal;
                result = sb.InvokeReturnAsIs(args);
            }
            finally
            {
                // and restore the scriptblocks session state...
                sb.SessionStateInternal = oldSessionState;
            }
            return result;
        }

        /// <summary>
        /// This routine allows you to get access variable objects in the callers module
        /// or from the toplevel sessionstate if there is no calling module.
        /// </summary>
        /// <param name="variableName"></param>
        /// <returns></returns>
        public PSVariable GetVariableFromCallersModule(string variableName)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                throw new ArgumentNullException("variableName");
            }

            var context = LocalPipeline.GetExecutionContextFromTLS();
            SessionState callersSessionState = null;
            foreach (var sf in context.Debugger.GetCallStack())
            {
                var frameModule = sf.InvocationInfo.MyCommand.Module;
                if (frameModule == null)
                {
                    break;
                }

                if (frameModule.SessionState != _sessionState)
                {
                    callersSessionState = sf.InvocationInfo.MyCommand.Module.SessionState;
                    break;
                }
            }
            if (callersSessionState != null)
            {
                return callersSessionState.Internal.GetVariable(variableName);
            }
            else
            {
                return context.TopLevelSessionState.GetVariable(variableName);
            }
        }

        /// <summary>
        /// Copies the local variables in the caller's cope into the module...
        /// </summary>
        internal void CaptureLocals()
        {
            if (_sessionState == null)
            {
                throw PSTraceSource.NewInvalidOperationException(Modules.InvalidOperationOnBinaryModule);
            }

            var context = LocalPipeline.GetExecutionContextFromTLS();
            var tuple = context.EngineSessionState.CurrentScope.LocalsTuple;
            IEnumerable<PSVariable> variables = context.EngineSessionState.CurrentScope.Variables.Values;
            if (tuple != null)
            {
                var result = new Dictionary<string, PSVariable>();
                tuple.GetVariableTable(result, false);
                variables = result.Values.Concat(variables);
            }

            foreach (PSVariable v in variables)
            {
                try
                {
                    // Only copy simple mutable variables...
                    if (v.Options == ScopedItemOptions.None && !(v is NullVariable))
                    {
                        PSVariable newVar = new PSVariable(v.Name, v.Value, v.Options, v.Attributes, v.Description);
                        _sessionState.Internal.NewVariable(newVar, false);
                    }
                }
                catch (SessionStateException)
                {
                }
            }
        }

        /// <summary>
        /// Build a custom object out of this module...
        /// </summary>
        /// <returns>A custom object</returns>
        public PSObject AsCustomObject()
        {
            if (_sessionState == null)
            {
                throw PSTraceSource.NewInvalidOperationException(Modules.InvalidOperationOnBinaryModule);
            }

            PSObject obj = new PSObject();

            foreach (KeyValuePair<string, FunctionInfo> entry in this.ExportedFunctions)
            {
                FunctionInfo func = entry.Value;
                if (func != null)
                {
                    PSScriptMethod sm = new PSScriptMethod(func.Name, func.ScriptBlock);
                    obj.Members.Add(sm);
                }
            }

            foreach (KeyValuePair<string, PSVariable> entry in this.ExportedVariables)
            {
                PSVariable var = entry.Value;
                if (var != null)
                {
                    PSVariableProperty sm = new PSVariableProperty(var);
                    obj.Members.Add(sm);
                }
            }

            return obj;
        }

        /// <summary>
        /// Optional script that is going to be called just before Remove-Module cmdlet removes the module
        /// </summary>
        public ScriptBlock OnRemove { get; set; }

        private ReadOnlyCollection<string> _exportedFormatFiles = new ReadOnlyCollection<string>(new List<string>());

        /// <summary>
        /// The list of Format files imported by this module.
        /// </summary>
        public ReadOnlyCollection<string> ExportedFormatFiles
        {
            get
            {
                return this._exportedFormatFiles;
            }
        }
        internal void SetExportedFormatFiles(ReadOnlyCollection<string> files)
        {
            this._exportedFormatFiles = files;
        }

        private ReadOnlyCollection<string> _exportedTypeFiles = new ReadOnlyCollection<string>(new List<string>());

        /// <summary>
        /// The list of types files imported by this module.
        /// </summary>
        public ReadOnlyCollection<string> ExportedTypeFiles
        {
            get
            {
                return this._exportedTypeFiles;
            }

        }
        internal void SetExportedTypeFiles(ReadOnlyCollection<string> files)
        {
            this._exportedTypeFiles = files;
        }

        /// <summary>
        /// Implements deep copy of a PSModuleInfo instance.
        /// <returns>A new PSModuleInfo instance</returns>
        /// </summary>
        public PSModuleInfo Clone()
        {
            PSModuleInfo clone = (PSModuleInfo)this.MemberwiseClone();

            clone._fileList = new List<string>(this.FileList);
            clone._moduleList = new Collection<object>(this._moduleList);

            foreach (var n in this.NestedModules)
            {
                clone.AddNestedModule(n);
            }

            clone._readonlyNestedModules = new ReadOnlyCollection<PSModuleInfo>(this.NestedModules);
            clone._readonlyRequiredModules = new ReadOnlyCollection<PSModuleInfo>(this.RequiredModules);
            clone._readonlyRequiredModulesSpecification = new ReadOnlyCollection<ModuleSpecification>(this.RequiredModulesSpecification);
            clone._requiredAssemblies = new Collection<string>(this._requiredAssemblies);
            clone._requiredModulesSpecification = new List<ModuleSpecification>();
            clone._requiredModules = new List<PSModuleInfo>();

            foreach (var r in this._requiredModules)
            {
                clone.AddRequiredModule(r);
            }
            foreach (var r in this._requiredModulesSpecification)
            {
                clone.AddRequiredModuleSpecification(r);
            }

            clone._scripts = new List<string>(this.Scripts);

            clone._sessionState = this.SessionState;

            return clone;
        }

        /// <summary>
        /// Enables or disables the appdomain module path cache 
        /// </summary>
        static public bool UseAppDomainLevelModuleCache { get; set; }

        /// <summary>
        /// Clear out the appdomain-level module path cache.
        /// </summary>
        static public void ClearAppDomainLevelModulePathCache()
        {
            _appdomainModulePathCache.Clear();
        }


#if DEBUG
        /// <summary>
        /// A method available in debug mode providing access to the module path cache.
        /// </summary>
        /// <returns></returns>
        static public object GetAppDomainLevelModuleCache()
        {
            return _appdomainModulePathCache;
        }
#endif
        /// <summary>
        /// Look up a module in the appdomain wide module path cache.
        /// </summary>
        /// <param name="moduleName">Module name to look up.</param>
        /// <returns>The path to the matched module</returns>
        static internal string ResolveUsingAppDomainLevelModuleCache(string moduleName)
        {
            string path;
            if (_appdomainModulePathCache.TryGetValue(moduleName, out path))
            {
                return path;
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Add an entry to the appdomain level module path cache. By default, if there already is an entry
        /// it won't be replace. If force is specified, then it will be updated. \
        /// </summary>
        /// <param name="moduleName"></param>
        /// <param name="path"></param>
        /// <param name="force"></param>
        static internal void AddToAppDomainLevelModuleCache(string moduleName, string path, bool force)
        {
            if (force)
            {
                _appdomainModulePathCache.AddOrUpdate(moduleName, path, (modulename, oldPath) => path);
            }
            else
            {
                _appdomainModulePathCache.TryAdd(moduleName, path);
            }
        }

        /// <summary>
        /// If there is an entry for the named module in the appdomain level module path cache, remove it.
        /// </summary>
        /// <param name="moduleName">The name of the module to remove from the cache</param>
        /// <returns>True if the module was remove.</returns>
        static internal bool RemoveFromAppDomainLevelCache(string moduleName)
        {
            string outString;
            return _appdomainModulePathCache.TryRemove(moduleName, out outString);
        }

        private readonly static System.Collections.Concurrent.ConcurrentDictionary<string, string> _appdomainModulePathCache = 
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    } // PSModuleInfo

    /// <summary>
    /// Indicates the type of a module.
    /// </summary>
    public enum ModuleType
    {
        /// <summary>
        /// Indicates that this is a script module (a powershell file with a .PSM1 extension)
        /// </summary>
        Script = 0,
        /// <summary>
        /// Indicates that this is compiled .dll containing cmdlet definitions.
        /// </summary>
        Binary = 1,
        /// <summary>
        /// Indicates that this module entry was derived from a module manifest and
        /// may have child modules.
        /// </summary>
        Manifest,
        /// <summary>
        /// Indicates that this is cmdlets-over-objects module (a powershell file with a .CDXML extension)
        /// </summary>
        Cim,
        /// <summary>
        /// Indicates that this is workflow module (a powershell file with a .XAML extension)
        /// </summary>
        Workflow,
    }

    /// <summary>
    /// Defines the possible access modes for a module...
    /// </summary>
    public enum ModuleAccessMode
    {
        /// <summary>
        /// The default access mode for the module
        /// </summary>
        ReadWrite = 0,
        /// <summary>
        /// The module is readonly and can only be removed with -force
        /// </summary>
        ReadOnly = 1,
        /// <summary>
        /// The module cannot be removed.
        /// </summary>
        Constant = 2
    }


    /// <summary>
    /// An EqualityComparer to compare 2 PSModuleInfo instances. 2 PSModuleInfos are
    /// considered equal if their Name,Guid and Version are equal.
    /// </summary>
    internal sealed class PSModuleInfoComparer : IEqualityComparer<PSModuleInfo>
    {
        public bool Equals(PSModuleInfo x, PSModuleInfo y)
        {
            //Check whether the compared objects reference the same data.
            if (Object.ReferenceEquals(x, y)) return true;

            //Check whether any of the compared objects is null.
            if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                return false;
            
            bool result = string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
                (x.Guid == y.Guid) && (x.Version == y.Version);

            return result;
        }

        public int GetHashCode(PSModuleInfo obj)
        {
            unchecked // Overflow is fine, just wrap     
            {
                int result = 0;

                if (obj != null)
                {
                    // picking two different prime numbers to avoid collisions
                    result = 23;
                    if (obj.Name != null)
                    {
                        result = result * 17 + obj.Name.GetHashCode();
                    }

                    if (obj.Guid != Guid.Empty)
                    {
                        result = result * 17 + obj.Guid.GetHashCode();
                    }

                    if (obj.Version != null)
                    {
                        result = result * 17 + obj.Version.GetHashCode();
                    }
                }

                return result;
            }
        }
    }

} // System.Management.Automation