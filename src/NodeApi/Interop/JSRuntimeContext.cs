// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JavaScript.NodeApi.Runtime;
using static Microsoft.JavaScript.NodeApi.Interop.JSCollectionProxies;
using static Microsoft.JavaScript.NodeApi.Runtime.JSRuntime;

namespace Microsoft.JavaScript.NodeApi.Interop;

/// <summary>
/// Manages JavaScript interop context for the lifetime of the .NET Node API host.
/// </summary>
/// <remarks>
/// A <see cref="JSRuntimeContext"/> instance is constructed when the .NET Node API managed host is
/// loaded, and disposed when the host is unloaded. (For AOT there is no "host" component, so each
/// AOT module has a context that matches the module lifetime.) The context tracks several kinds
/// of JS references used internally by this assembly, so that the references can be re-used for
/// the lifetime of the host and disposed when the context is disposed.
/// </remarks>
public sealed class JSRuntimeContext : IDisposable
{
    /// <summary>
    /// Name of a global object that may hold context specific to Node API .NET.
    /// </summary>
    /// <remarks>
    /// Currently it is only used to pass the require() function to .NET AOT modules.
    /// </remarks>
    public const string GlobalObjectName = "node_api_dotnet";

    private readonly napi_env _env;

    // Track JS constructors and instance JS wrappers for exported classes, enabling
    // .NET objects to be automatically wrapped when returned to JS, and re-wrapped as needed
    // if the (weakly-referenced) JS wrapper has been released.

    // TODO: Consider an optimization that avoids dictionary lookups:
    // If an exported class is declared as partial, then the generator can add a
    // static `JSConstructor` property and an instance `JSWrapper` property to the class.
    // (The dictionary mappings could still be used to export external/non-partial classes.)

    /// <summary>
    /// Maps from exported class types to (strong references to) JS constructors for each class.
    /// </summary>
    /// <remarks>
    /// Used to automatically construct a JS wrapper object with correct prototype whenever
    /// a class instance is marshalled from C# to JS.
    /// </remarks>
    private readonly ConcurrentDictionary<Type, JSReference> _classMap = new();

    /// <summary>
    /// Maps from exported static class names to (strong references to) JS objects for each class.
    /// </summary>
    /// <remarks>
    /// Used primarily to prevent the JS GC from collecting the class object, which can cause
    /// class property descriptors to be finalized while a class method is still referenced and
    /// called from JS.
    /// </remarks>
    private readonly ConcurrentDictionary<string, JSReference> _staticClassMap = new();

    /// <summary>
    /// Maps from (weak references to) C# class objects to (weak references to) JS wrappers for
    /// each object.
    /// </summary>
    /// <remarks>
    /// Enables re-using the same JS wrapper objects for the same C# objects, so that
    /// a C# object maps to the same JS instance when marshalled multiple times. The
    /// references are weak to allow the JS wrappers to be released; new wrappers are
    /// re-constructed as needed. This is not used with C# structs which are always
    /// passed to/from JS by value.
    /// </remarks>
    private readonly ConditionalWeakTable<object, JSReference> _objectMap = new();

    /// <summary>
    /// Maps from exported struct types to (strong references to) JS constructors for classes
    /// that represent each struct.
    /// </summary>
    /// <remarks>
    /// Used to automatically construct a JS object with correct prototype whenever
    /// a struct is marshalled from C# to JS. Since structs are marshalled by value,
    /// the JS object is not a wrapper, rather the properties are copied by the marshaller.
    /// </remarks>
    private readonly ConcurrentDictionary<Type, JSReference> _structMap = new();

    /// <summary>
    /// Maps from JS class names to (strong references to) JS constructors for classes imported
    /// from JS to C#.
    /// </summary>
    /// <remarks>
    /// Enables C# code to construct instances of built-in JS classes, without having to resolve
    /// the constructors every time.
    /// </remarks>
    private readonly ConcurrentDictionary<(string?, string?), JSReference> _importMap = new();

    /// <summary>
    /// Holds a reference to the synchronous CommonJS require() function.
    /// </summary>
    private JSReference? _requireFunction;

    /// <summary>
    /// Holds a reference to the asynchronous ES import() function.
    /// </summary>
    private JSReference? _importFunction;

    private readonly ConcurrentDictionary<Type, JSProxy.Handler> _collectionProxyHandlerMap = new();

    internal napi_env EnvironmentHandle
    {
        get
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(JSRuntimeContext));
            }

            return _env;
        }
    }

    public static explicit operator napi_env(JSRuntimeContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        return context.EnvironmentHandle;
    }

    public static explicit operator JSRuntimeContext(napi_env env)
        => JSValue.GetInstanceData(env) as JSRuntimeContext
           ?? throw new InvalidCastException("Context is not found in napi_env instance data.");

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Gets the current runtime context.
    /// </summary>
    /// <exception cref="InvalidOperationException">No runtime context was set for the current
    /// thread.</exception>
    public static JSRuntimeContext Current => JSValueScope.Current.RuntimeContext;

    public JSRuntime Runtime { get; }

    public JSSynchronizationContext SynchronizationContext { get; }

    internal JSRuntimeContext(
        napi_env env,
        JSRuntime runtime,
        JSSynchronizationContext? synchronizationContext = null)
    {
        if (env.IsNull) throw new ArgumentNullException(nameof(env));

        _env = env;
        Runtime = runtime;
        JSValue.SetInstanceData(env, this);
        SynchronizationContext = synchronizationContext ?? JSSynchronizationContext.Create();
    }

    /// <summary>
    /// Gets or sets the require() function, that supports synchronously importing CommonJS modules.
    /// </summary>
    /// <remarks>
    /// Managed-host initialization will typically pass in the require function.
    /// </remarks>
    public JSFunction RequireFunction
    {
        get
        {
            JSValue? value = _requireFunction?.GetValue();
            if (value?.IsFunction() == true)
            {
                return (JSFunction)value.Value;
            }

            JSValue globalObject = JSValue.Global[GlobalObjectName];
            if (globalObject.IsObject())
            {
                JSValue globalRequire = globalObject["require"];
                if (globalRequire.IsFunction())
                {
                    _requireFunction = new JSReference(globalRequire);
                    return (JSFunction)globalRequire;
                }
            }

            throw new InvalidOperationException(
                $"The require function was not found on the global {GlobalObjectName} object. " +
                $"Set `global.{GlobalObjectName}.require` before loading the module.");
        }
        set
        {
            _requireFunction?.Dispose();
            _requireFunction = new JSReference(value);
        }
    }

    /// <summary>
    /// Gets or sets the import() function, that supports asynchronously importing ES modules.
    /// </summary>
    /// <remarks>
    /// Managed-host initialization will typically pass in the import function.
    /// </remarks>
    public JSFunction ImportFunction
    {
        get
        {
            JSValue? value = _importFunction?.GetValue();
            if (value?.IsFunction() == true)
            {
                return (JSFunction)value.Value;
            }

            JSValue globalObject = JSValue.Global[GlobalObjectName];
            if (globalObject.IsObject())
            {
                JSValue globalImport = globalObject["import"];
                if (globalImport.IsFunction())
                {
                    _importFunction = new JSReference(globalImport);
                    return (JSFunction)globalImport;
                }
            }

            throw new InvalidOperationException(
                $"The import function was not found on the global {GlobalObjectName} object. " +
                $"Set `global.{GlobalObjectName}.import` before loading the module.");
        }
        set
        {
            _importFunction?.Dispose();
            _importFunction = new JSReference(value);
        }
    }

    /// <summary>
    /// Registers a class JS constructor, enabling automatic JS wrapping of instances of the class.
    /// </summary>
    /// <param name="constructorFunction">JS class constructor function returned from
    /// <see cref="JSNativeApi.DefineClass"/></param>
    /// <returns>The JS constructor.</returns>
    internal JSValue RegisterClass<T>(JSValue constructorFunction) where T : class
    {
        _classMap.AddOrUpdate(
            typeof(T),
            (_) => new JSReference(constructorFunction, isWeak: false),
            (_, _) => throw new InvalidOperationException(
                "Class already registered for JS export: " + typeof(T)));
        return constructorFunction;
    }

    /// <summary>
    /// Registers a static class JS object, preventing it from being GC'd before the module is
    /// unloaded.
    /// </summary>
    /// <param name="name">Name of the static class.</param>
    /// <param name="classObject">Object that has the class properties and methods.</param>
    /// <returns>The JS object.</returns>
    internal JSValue RegisterStaticClass(string name, JSValue classObject)
    {
        _staticClassMap.AddOrUpdate(
            name,
            (_) => new JSReference(classObject, isWeak: false),
            (_, _) => throw new InvalidOperationException(
                "Class already registered for JS export: " + name));
        return classObject;
    }

    /// <summary>
    /// Gets a class JS constructor that was previously registered.
    /// </summary>
    private JSValue GetClassConstructor<T>() where T : class
    {
        if (!_classMap.TryGetValue(typeof(T), out JSReference? constructorReference))
        {
            throw new InvalidOperationException(
                "Class not registered for JS export: " + typeof(T));
        }

        JSValue? constructorFunction = constructorReference!.GetValue();
        if (!constructorFunction.HasValue)
        {
            // This should never happen because the reference is "strong".
            throw new InvalidOperationException("Failed to resolve class constructor reference.");
        }

        return constructorFunction.Value;
    }

    /// <summary>
    /// Attaches an object to a JS wrapper, and saves a weak reference to the wrapper.
    /// </summary>
    /// <param name="wrapper">JS object passed as the 'this' argument to the constructor callback
    /// for <see cref="JSNativeApi.DefineClass"/>.</param>
    /// <param name="externalInstance">New or existing instance of the class to be wrapped,
    /// passed as a JS "external" value.</param>
    /// <returns>The JS wrapper.</returns>
    internal JSValue InitializeObjectWrapper(JSValue wrapper, JSValue externalInstance)
    {
        object obj = externalInstance.GetValueExternal();

        // The reference returned by Wrap() is weak (refcount=0), which is good:
        // if the JS object is released then the reference will fail to resolve, and
        // GetOrCreateObjectWrapper() will create a new JS wrapper if requested.
        wrapper.Wrap(obj, out JSReference wrapperWeakRef);

        // There should not be an existing wrapper in the map, because this is the
        // first initialization of a wrapper for the .NET object.
        _objectMap.Add(obj, wrapperWeakRef);

        return wrapper;
    }

    /// <summary>
    /// Gets or creates a JS wrapper for an instance of a class.
    /// </summary>
    /// <returns>The JS wrapper.</returns>
    /// <remarks>
    /// If the class was constructed via JS, then the wrapper created at that time will be
    /// found in the map and returned, if the weak reference to it is still valid. Otherwise
    /// a new JS object is constructed to wrap the existing instance, and a weak reference to
    /// the new wrapper is saved in the map.
    /// </remarks>
    public JSValue GetOrCreateObjectWrapper<T>(T obj) where T : class
    {
        if (obj == null)
        {
            // Marshal null object reference to JS undefined.
            return JSValue.Undefined;
        }

        JSValue? wrapper = null;
        JSReference CreateWrapper(T obj)
        {
            if (obj is Stream stream)
            {
                wrapper = NodeStream.CreateProxy(stream);
            }
            else
            {
                // Pass the existing instance as an external value to the JS constructor.
                // The constructor callback will then use that instead of creating a new
                // instance of the class.
                JSValue externalValue = JSValue.CreateExternal(obj);
                JSValue constructorFunction = GetClassConstructor<T>();
                wrapper = constructorFunction.CallAsConstructor(externalValue);
            }

            return new(wrapper.Value, isWeak: true);
        }

        if (!_objectMap.TryGetValue(obj, out JSReference? wrapperReference))
        {
            // No wrapper was found in the map for the object. Create a new one.
            wrapperReference = CreateWrapper(obj);

            // Use AddOrUpdate() in case the constructor just added the object.
#if NETFRAMEWORK || NETSTANDARD
            _objectMap.Remove(obj);
            _objectMap.Add(obj, wrapperReference);
#else
            _objectMap.AddOrUpdate(obj, wrapperReference);
#endif
        }
        else
        {
            wrapper = wrapperReference.GetValue();
            if (!wrapper.HasValue)
            {
                // A reference was found in the map, but the JS object was released.
                // Create a new wrapper JS object and update the reference in the map.
                wrapperReference.Dispose();
                wrapperReference = CreateWrapper(obj);
#if NETFRAMEWORK || NETSTANDARD
                _objectMap.Remove(obj);
                _objectMap.Add(obj, wrapperReference);
#else
                _objectMap.AddOrUpdate(obj, wrapperReference);
#endif
            }
        }

        return wrapper!.Value;
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        IEnumerable<T> collection,
        JSValue.From<T> toJS)
    {
        return collection is JSIterableEnumerable<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IEnumerable<T>),
                    (_) => CreateIterableProxyHandlerForEnumerable(toJS));
                return new JSProxy(new JSObject(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        IAsyncEnumerable<T> collection,
        JSValue.From<T> toJS)
    {
        return collection is JSIterableEnumerable<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IAsyncEnumerable<T>),
                    (_) => CreateAsyncIterableProxyHandlerForAsyncEnumerable(toJS));
                return new JSProxy(new JSObject(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        IReadOnlyCollection<T> collection,
        JSValue.From<T> toJS)
    {
        return collection is JSArrayReadOnlyCollection<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IReadOnlyCollection<T>),
                    (_) => CreateIterableProxyHandlerForReadOnlyCollection(toJS));
                return new JSProxy(new JSObject(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        ICollection<T> collection,
        JSValue.From<T> toJS,
        JSValue.To<T> fromJS)
    {
        return collection is JSArrayCollection<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(ICollection<T>),
                    (_) => CreateIterableProxyHandlerForCollection(toJS, fromJS));
                return new JSProxy(new JSObject(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        IReadOnlyList<T> collection,
        JSValue.From<T> toJS)
    {
        return collection is JSArrayReadOnlyList<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IReadOnlyList<T>),
                    (_) => CreateArrayProxyHandlerForReadOnlyList(toJS));
                return new JSProxy(new JSArray(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<T>(
        IList<T> collection,
        JSValue.From<T> toJS,
        JSValue.To<T> fromJS)
    {
        return collection is JSArrayList<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IList<T>),
                    (_) => CreateArrayProxyHandlerForList(toJS, fromJS));
                return new JSProxy(new JSArray(), proxyHandler, collection);
            });
    }

#if READONLY_SET
    public JSValue GetOrCreateCollectionWrapper<T>(
        IReadOnlySet<T> collection,
        JSValue.From<T> toJS,
        JSValue.To<T> fromJS)
    {
        return collection is JSSetReadOnlySet<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(IReadOnlySet<T>),
                    (_) => CreateSetProxyHandlerForReadOnlySet(toJS, fromJS));
                return new JSProxy(new JSSet(), proxyHandler, collection);
            });
    }
#endif

    public JSValue GetOrCreateCollectionWrapper<T>(
        ISet<T> collection,
        JSValue.From<T> toJS,
        JSValue.To<T> fromJS)
    {
        return collection is JSSetSet<T> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                    typeof(ISet<T>),
                    (_) => CreateSetProxyHandlerForSet(toJS, fromJS));
                return new JSProxy(new JSSet(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> collection,
        JSValue.From<TKey> keyToJS,
        JSValue.From<TValue> valueToJS,
        JSValue.To<TKey> keyFromJS)
    {
        return collection is JSMapReadOnlyDictionary<TKey, TValue> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                typeof(IReadOnlyDictionary<TKey, TValue>),
                    (_) => CreateMapProxyHandlerForReadOnlyDictionary(
                        keyToJS, valueToJS, keyFromJS));
                return new JSProxy(new JSMap(), proxyHandler, collection);
            });
    }

    public JSValue GetOrCreateCollectionWrapper<TKey, TValue>(
        IDictionary<TKey, TValue> collection,
        JSValue.From<TKey> keyToJS,
        JSValue.From<TValue> valueToJS,
        JSValue.To<TKey> keyFromJS,
        JSValue.To<TValue> valueFromJS)
    {
        return collection is JSMapDictionary<TKey, TValue> adapter ? adapter.Value :
            GetOrCreateCollectionProxy(collection, () =>
            {
                JSProxy.Handler proxyHandler = _collectionProxyHandlerMap.GetOrAdd(
                typeof(IDictionary<TKey, TValue>),
                    (_) => CreateMapProxyHandlerForDictionary(
                        keyToJS, valueToJS, keyFromJS, valueFromJS));
                return new JSProxy(new JSMap(), proxyHandler, collection);
            });
    }

    private JSValue GetOrCreateCollectionProxy(
        object collection,
        Func<JSValue> createWrapper)
    {
        JSValue? wrapper = null;

        if (!_objectMap.TryGetValue(collection, out JSReference? wrapperReference))
        {
            // No wrapper was found in the map for the object. Create a new one.
            wrapper = createWrapper();
            _objectMap.Add(collection, new JSReference(wrapper.Value, isWeak: true));
        }
        else
        {
            wrapper = wrapperReference.GetValue();
            if (!wrapper.HasValue)
            {
                // A reference was found in the map, but the JS object was released.
                // Create a new wrapper JS object and update the reference in the map.
                wrapperReference.Dispose();
                wrapper = createWrapper();
                wrapperReference = new JSReference(wrapper.Value, isWeak: true);
#if NETFRAMEWORK || NETSTANDARD
                _objectMap.Remove(collection);
                _objectMap.Add(collection, wrapperReference);
#else
                _objectMap.AddOrUpdate(collection, wrapperReference);
#endif
            }
        }

        return wrapper!.Value;
    }

    /// <summary>
    /// Registers a struct JS constructor, enabling instantiation of JS wrappers for the struct.
    /// </summary>
    /// <param name="constructorFunction">JS struct constructor function returned from
    /// <see cref="JSNativeApi.DefineClass"/></param>
    /// <returns>The JS constructor.</returns>
    internal JSValue RegisterStruct<T>(JSValue constructorFunction) where T : struct
    {
        _structMap.AddOrUpdate(
            typeof(T),
            (_) => new JSReference(constructorFunction, isWeak: false),
            (_, _) => throw new InvalidOperationException(
                "Struct already registered for JS export: " + typeof(T)));
        return constructorFunction;
    }

    /// <summary>
    /// Creates a new (empty) JS instance for a struct.
    /// </summary>
    /// <returns>The JS wrapper.</returns>
    public JSValue CreateStruct<T>() where T : struct
    {
        if (!_structMap.TryGetValue(typeof(T), out JSReference? constructorReference))
        {
            throw new InvalidOperationException(
                "Struct not registered for JS export: " + typeof(T));
        }

        JSValue? constructorFunction = constructorReference!.GetValue();
        if (!constructorFunction.HasValue)
        {
            // This should never happen because the reference is "strong".
            throw new InvalidOperationException("Failed to resolve struct constructor reference.");
        }

        return constructorFunction.Value.CallAsConstructor();
    }

    /// <summary>
    /// Imports a module or module property from JavaScript.
    /// </summary>
    /// <param name="module">Name of the module being imported, or null to import a
    /// global property. This is equivalent to the value provided to <c>import</c> or
    /// <c>require()</c> in JavaScript. Required if <paramref name="property"/> is null.</param>
    /// <param name="property">Name of a property on the module (or global), or null to import
    /// the module object. Required if <paramref name="module"/> is null.</param>
    /// <param name="esModule">True to import an ES module; false to import a CommonJS module
    /// (default).</param>
    /// <returns>The imported value. When importing from an ES module, this is a JS promise
    /// that resolves to the imported value.</returns>
    /// <exception cref="ArgumentNullException">Both <paramref cref="module" /> and
    /// <paramref cref="property" /> are null.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="RequireFunction" /> or
    /// <see cref="ImportFunction" /> property was not initialized.</exception>
    public JSValue Import(
        string? module,
        string? property = null,
        bool esModule = false)
    {
        if ((module == null || module == "global") && property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        JSReference reference = _importMap.GetOrAdd((module, property), (_) =>
        {
            if (module == null || module == "global")
            {
                // Importing a built-in object from `global`.
                JSValue value = JSValue.Global[property!];
                return new JSReference(value);
            }
            else if (property == null)
            {
                // Importing from a module via require() or import().
                JSFunction requireOrImport = esModule ? ImportFunction : RequireFunction;
                JSValue moduleValue = requireOrImport.CallAsStatic(module);
                return new JSReference(moduleValue);
            }
            else
            {
                // Getting a property on a module - import the module first.
                JSValue moduleValue = Import(module, property: null, esModule);
                if (esModule)
                {
                    return new JSReference(((JSPromise)moduleValue).Then(
                        (value) => value.IsUndefined() ?
                            JSValue.Undefined : value.GetProperty(property)));
                }
                else
                {
                    JSValue propertyValue = moduleValue.IsUndefined() ?
                        JSValue.Undefined : moduleValue.GetProperty(property);
                    return new JSReference(propertyValue);
                }
            }

        });
        return reference.GetValue() ?? JSValue.Undefined;
    }

    /// <summary>
    /// Imports a module or module property from JavaScript.
    /// </summary>
    /// <param name="module">Name of the module being imported, or null to import a
    /// global property. This is equivalent to the value provided to <c>import</c> or
    /// <c>require()</c> in JavaScript. Required if <paramref name="property"/> is null.</param>
    /// <param name="property">Name of a property on the module (or global), or null to import
    /// the module object. Required if <paramref name="module"/> is null.</param>
    /// <param name="esModule">True to import an ES module; false to import a CommonJS module
    /// (default).</param>
    /// <returns>A task that results in the imported value. When importing from an ES module,
    /// the task directly results in the imported value (not a JS promise).</returns>
    /// <exception cref="ArgumentNullException">Both <paramref cref="module" /> and
    /// <paramref cref="property" /> are null.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="RequireFunction" /> or
    /// <see cref="ImportFunction" /> property was not initialized.</exception>
    public async Task<JSValue> ImportAsync(
        string? module,
        string? property = null,
        bool esModule = false)
    {
        JSValue value = Import(module, property, esModule);

        if (value.IsPromise())
        {
            value = await ((JSPromise)value).AsTask();
        }

        return value;
    }

    public void Dispose()
    {
        if (IsDisposed) return;

        IsDisposed = true;

        SynchronizationContext.Dispose();

#if !(NETFRAMEWORK || NETSTANDARD)
        // ConditionalWeakTable<> is not enumerable in .NET Framework.
        // The JS references will still be released eventually by their finalizers.
        DisposeReferences(_objectMap.Select((entry) => entry.Value));
#endif
        DisposeReferences(_classMap.Values);
        DisposeReferences(_staticClassMap.Values);
        DisposeReferences(_structMap.Values);
    }

    private static void DisposeReferences(
        IEnumerable<JSReference> references)
    {
        foreach (JSReference reference in references)
        {
            try
            {
                reference.Dispose();
            }
            catch (JSException)
            {
            }
        }
    }


    private long _gcHandleCount;

    /// <summary>
    /// Gets the count of GC handles allocated in the current runtime context. Useful for
    /// detecting memory leaks related to .NET + JS interop.
    /// </summary>
    /// <remarks>
    /// JS objects use GC handles to hold onto .NET objects. When a JS object is garbage-
    /// collected, the GC handle it holds (if any) is freed, and then the .NET object becomes
    /// available for .NET garbage-collection (assuming there are no other GC handles or .NET
    /// references to the same object).
    /// </remarks>
    public long GCHandleCount => _gcHandleCount;

#if DEBUG

    /// <summary>
    /// Records the target object and allocation stack trace of a GC handle.
    /// Useful for debugging GC handle leaks. (Only enabled in debug mode.)
    /// </summary>
    public class GCHandleInfo
    {
        public object? Value { get; set; }
        public StackTrace AllocationStackTrace { get; set; } = null!;
    }

    /// <summary>
    /// Maps from GC handles to information about each handle. Useful for debugging
    /// GC handle leaks. (Only enabled in debug mode.)
    /// </summary>
    public Dictionary<nint, GCHandleInfo> GCHandleMap { get; }
        = new Dictionary<nint, GCHandleInfo>();

#endif

    /// <summary>
    /// Allocates a GC handle and tracks the allocation on this runtime context. Call this
    /// method instead of <see cref="GCHandle.Alloc(object?)" /> to track handle allocations.
    /// </summary>
    internal GCHandle AllocGCHandle(object value)
    {
        GCHandle handle = GCHandle.Alloc(value);

        Interlocked.Increment(ref _gcHandleCount);

#if DEBUG
        string targetType = value?.GetType().Name ?? "null";
        Debug.WriteLine($"Allocating GC handle {(nint)handle:X16}: {targetType}");
        GCHandleMap[(nint)handle] = new GCHandleInfo
        {
            Value = value,
            AllocationStackTrace = new StackTrace(skipFrames: 1),
        };
#endif
        return handle;
    }

    /// <summary>
    /// Frees a GC handle previously allocated via <see cref="AllocGCHandle(object)" />
    /// and tracked on this runtime context.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle was not previously allocated
    /// by <see cref="AllocGCHandle(object)" />, or was already freed.</exception>
    internal void FreeGCHandle(GCHandle handle)
    {
        Interlocked.Decrement(ref _gcHandleCount);

#if DEBUG
        string targetType = handle.Target?.GetType().Name ?? "null";
        Debug.WriteLine($"Freeing GC handle {(nint)handle:X16}: {targetType}");
        if (!GCHandleMap.Remove((nint)handle))
        {
            throw new InvalidOperationException(
                $"Freed GC handle to {targetType} was not in the handle map.");
        }
#endif

        handle.Free();
    }

#if NETFRAMEWORK || NETSTANDARD
    private class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
#endif
}
