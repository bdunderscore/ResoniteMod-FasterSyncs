using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Elements.Core;
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;

namespace MeshLoadTweak
{
    public class Loader : ResoniteMod
    {
        public static ModConfiguration config;

        public override string Name => "FasterSyncs";
        public override string Author => "bd_";
        public override string Version => "0.0.1";
        public override string Link => "https://github.com/bdunderscore/ResoniteMod-FasterSyncs";
    
        public override void OnEngineInit()
        {
            UniLog.FlushEveryMessage = true;
            UniLog.Log("Initializing FasterSyncs...");

            try
            {
                //config = GetConfiguration();
                Harmony.DEBUG = true;
                Harmony harmony = new Harmony("nadena.dev.FasterSyncs");
                
                var engineRecordUploadTask =  typeof(EngineRecordUploadTask);
                var recordUploadTaskBase = engineRecordUploadTask.BaseType;

                AddTimingForTaskMethod(harmony, AccessTools.Method(recordUploadTaskBase, "CheckCloudVersion"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(engineRecordUploadTask, "PrepareRecord"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(engineRecordUploadTask, "PrepareManifest"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(engineRecordUploadTask, "CollectAssets"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(engineRecordUploadTask, "UpdateMainAsset"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(recordUploadTaskBase, "PreprocessRecord"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(recordUploadTaskBase, "UploadAssets"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(recordUploadTaskBase, "UpsertRecord"));
                
                AddTimingForTaskMethod(harmony, AccessTools.Method(typeof(AssetInterface), "GetGlobalAssetInfo"));
                //AddTimingForTaskMethod(harmony, AccessTools.Method(typeof(AssetInterface), "StoreAssetMetadata"));
                //AddTimingForTaskMethod(harmony, AccessTools.Method(typeof(LocalDB), "StoreCacheRecordAsync"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(typeof(AssetUploadTask<CloudflareChunkResult>), "UploadAssetData"));
                AddTimingForTaskMethod(harmony, AccessTools.Method(typeof(AssetUploadTask<CloudMessage>), "UploadAssetData"));
                
                harmony.Patch(
                    AccessTools.Method(typeof(EngineRecordUploadTask), "CollectAssets"),
                    prefix: new HarmonyMethod(typeof(Loader), nameof(Before__CollectAssets))
                );
                
                harmony.Patch(
                    AccessTools.Method(typeof(AssetInterface), "GetGlobalAssetInfo"),
                    prefix: new HarmonyMethod(typeof(Loader), nameof(Before__GetGlobalAssetInfo))
                );
            }
            catch (Exception e)
            {
                UniLog.Error("[FasterSyncs] Failed to initialize: " + e);
            }
            finally
            {
                Harmony.DEBUG = false;
            }
        }
        
        private static readonly ThreadLocal<bool> _invokeRealCollectAssets = new ThreadLocal<bool>(() => false);
        private static bool Before__CollectAssets(EngineRecordUploadTask __instance, ref Task<bool> __result, CancellationToken cancelationToken)
        {
            // Try to fetch the prior record version, and collect assets we know exist.
            // To do this, we interrupt processing in order to create a wrapper task; we'll use a TaskLocal to avoid this
            // when we want to invoke the real deal.
            if (_invokeRealCollectAssets.Value) return true;
            
            __result = Wrap_CollectAssets(__instance, cancelationToken);
            return false;
        }
        
        private static Task<bool> InvokeOriginalCollectAssets(EngineRecordUploadTask task, CancellationToken token)
        {
            _invokeRealCollectAssets.Value = true;
            try
            {
                return (Task<bool>) AccessTools.Method(typeof(EngineRecordUploadTask), "CollectAssets")
                    .Invoke(task, new object[] { token });
            }
            finally
            {
                _invokeRealCollectAssets.Value = false;
            }
        }

        private static async Task<bool> Wrap_CollectAssets(EngineRecordUploadTask task, CancellationToken token)
        {
            var signatures =
                task.Record.Manifest
                    .Where(uri => Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                    .Select(uri => new Uri(uri))
                    .Where(uri => uri.Scheme == task.Cloud.Assets.DBScheme)
                    .Select(uri => task.Cloud.Assets.DBSignature(uri))
                    .ToList();

            using (new RecordPrefetcher(task.Cloud, signatures, token))
            {
                return await InvokeOriginalCollectAssets(task, token);
            }
        }

        
        private static bool Before__GetGlobalAssetInfo(
            ref Task<CloudResult<SkyFrost.Base.AssetInfo>> __result,
            AssetInterface __instance,
            string hash
        )
        {
            __result = RecordPrefetcher.TryGetGlobalAssetInfo(__instance, hash);
            return __result == null;
        }

        private void AddTimingForTaskMethod(Harmony harmony, MethodInfo mi)
        {
            var param_instructions = Expression.Parameter(typeof(IEnumerable<CodeInstruction>), "instructions");
            Expression transpilerExpression = Expression.Call(
                AccessTools.Method(typeof(Loader), nameof(Transpile)),
                new Expression[] {
                    Expression.Constant(mi),
                    param_instructions
                }
            );
            LambdaExpression lambdaExpression = Expression.Lambda(transpilerExpression, param_instructions);
            
            var assemblyName = new AssemblyName("DynamicAssembly_" + Guid.NewGuid().ToString("N"));
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("DynamicModule");
            var typeBuilder = moduleBuilder.DefineType("DynamicType", TypeAttributes.Public | TypeAttributes.Class);
            var methodBuilder = typeBuilder.DefineMethod(
                "Transpiler",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(IEnumerable<CodeInstruction>),
                new Type[] { typeof(IEnumerable<CodeInstruction>) });
            
            lambdaExpression.CompileToMethod(methodBuilder);
            
            var createdType = typeBuilder.CreateType();
            var transpilerMethod = createdType.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);

            HarmonyMethod hm = new HarmonyMethod(transpilerMethod);
            harmony.Patch(mi, transpiler: hm);
        }

        private static void AddTimingToFuture(Task future, string name)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            
            future.ContinueWith(t =>
            {
                stopwatch.Stop();
                UniLog.Log($"[FasterSyncs] {name} took {stopwatch.ElapsedMilliseconds} ms");
            });
        }
        
        public static IEnumerable<CodeInstruction> Transpile(MethodInfo method, IEnumerable<CodeInstruction> instructions)
        {
            var name = method.FullDescription();

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Ldstr, name);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Loader), nameof(AddTimingToFuture)));
                }
                
                yield return instruction;
            }
        }
    }
}
