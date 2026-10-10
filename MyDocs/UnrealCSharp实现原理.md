# UnrealCSharp 插件实现原理分析

> 分析对象：`Plugins/UnrealCSharp`（基于 .NET，支持 UE 5.0-5.7）
> 核心问题：C# 如何实现蓝图级别的功能？C# 如何继承蓝图、蓝图如何继承 C#？

## 0. 一句话总结

**该插件没有为 C# 类生成任何 C++ 占位代码，而是在运行时直接 `NewObject<UClass>` 手工构造出真正的 UClass，并打上 `CLASS_Native` 标志"伪装"成原生 C++ 类**——从而骗过 UE 的"蓝图只能继承原生类"的限制；函数调用则通过把 UFunction 的原生函数指针统一偷换成 `execCallCSharp` 这个共享 thunk，分发进 CLR。此外还有更轻量的 `[Override]` partial 模式——不建新类，直接把已有蓝图的事件函数指针偷换成同一个 thunk（见 4.4 节）。

---

## 1. 整体架构：三层结构

```
┌─────────────────────────────────────────────────────┐
│  C# 特性声明层                                       │
│  [UClass] [UFunction] [UProperty] [Override] ...    │
│  (Script/UE/Dynamic/ 下 250+ 个特性，对应 UE 元数据)  │
├─────────────────────────────────────────────────────┤
│  托管反射桥接层 (UnrealCSharpCore)                    │
│  FReflectionRegistry / FClassReflection             │
│  ← 通过反射调用 C# Utils.GetClassReflection 导出类型信息 │
├─────────────────────────────────────────────────────┤
│  UClass 动态构造层 (UnrealCSharpCore)                 │
│  FDynamicClassGenerator / FDynamicGeneratorCore     │
│  NewObject<UClass> + 手工填充反射数据（替代 UHT）      │
├─────────────────────────────────────────────────────┤
│  逻辑绑定层 (UnrealCSharp)                           │
│  FCSharpBind: SetNativeFunc(execCallCSharp) 接管函数  │
└─────────────────────────────────────────────────────┘
```

核心思路：正常 UE 开发中，UClass 由 UHT 生成的 `Z_ConstructUClass_*` 注册代码在 C++ 静态初始化时构造。本插件改为**运行时用托管反射扫描 C# 程序集，找到带特性的类型，再用引擎反射 API 手工完成 UHT 做的一切**（SetSuperStruct、设置 ClassFlags、new FProperty、NewObject<UFunction>、Bind/StaticLink、构造 CDO）。

---

## 2. 动态类：C# 如何变成真正的 UClass

### 2.1 C# 侧声明

```csharp
[UClass]                                    // 标记这是一个动态 UClass
public partial class AMyActor : AActor      // 继承任意已导出的 UE 类
{
    [UProperty]                             // 声明反射属性
    public float MySpeed { get; set; }

    [UFunction]                             // 声明反射函数（可被蓝图调用）
    public void DoSomething() { ... }

    [Override]                              // 覆写父类蓝图事件（如 BeginPlay）
    public override void ReceiveBeginPlay()
    {
        base.ReceiveBeginPlay();
    }
}
```

- 特性定义在 `Script/UE/Dynamic/` 下，按 Class/Function/Property/Struct/Enum/Interface 分目录，与 UE 的 `UCLASS()`/`UPROPERTY()` 元数据一一对应。
- 关键设计：`UClassAttribute`、`UFunctionAttribute` 等都继承自 `OverrideAttribute`（`Script/UE/CoreUObject/OverrideAttribute.cs`）——所有"进入 UE 反射体系的标记"共用一个基类，方便 C++ 侧统一过滤。
- `[PathName("/Game/...")]` 特性用于指定非默认路径名（C# 继承蓝图时使用，见第 4 节）。

### 2.2 C++ 侧扫描与注册

C++ 不自己解析 C#，而是通过托管反射调用 C# 助手类 `Script/UE/CoreUObject/Utils.cs`：

| 方法 | 作用 |
|---|---|
| `Utils.GetTypesWithAttribute` | 扫描程序集中带某特性的所有类型 |
| `Utils.GetClassReflection` | 一次性导出类型名、父类、接口、特性列表、属性、方法签名 |

C++ 侧对应物：

- **`FReflectionRegistry`**（`Source/UnrealCSharpCore/Public/Reflection/FReflectionRegistry.h`，单例）：类型注册中心，惰性创建并缓存 `FClassReflection`。
- **`FClassReflection`**（`FClassReflection.h/.cpp`）：C# `Type` 在 C++ 侧的镜像，持有 Parent/Properties/Methods/Interfaces 及 `HasAttribute` 查询。
- 程序集遍历入口：`FDynamicGeneratorCore::Generator()`（`FDynamicGeneratorCore.cpp:232`）遍历 `ScriptDomain->GetAssemblies()`，逐个调用 `GetClassesWithAttribute`。

### 2.3 UClass 的手工构造（核心）

文件：`Source/UnrealCSharpCore/Private/Dynamic/FDynamicClassGenerator.cpp`

**入口与依赖排序**：`FDynamicClassGenerator::Generator()` 以 `[UClass]` 为过滤条件收集类型，为每个类建依赖图节点（父类/属性类型/接口都是依赖），`FDynamicDependencyGraph` 做拓扑排序后按序生成。

**单类构造流程**：

```
LoadClass(父类路径)                        // 解析父 UClass (cpp:164)
    ↓
NewObject<UClass>(...)                    // 无 C++ 代码、无生成代码 (cpp:391)
    ↓
BeginGenerator (cpp:284-303)
    SetSuperStruct(ParentClass)
    ClassFlags |= CLASS_Native            // ★ 伪装成原生类
    ClassConstructor = &FDynamicClassGenerator::ClassConstructor
    ↓
ProcessGenerator (cpp:316-325)
    SetFlags: 把 Script.Dynamic 命名空间的特性映射为 CLASS_*/FUNC_*/CPF_* 标志
    GeneratorProperty: new FFloatProperty(...) 等，AddCppProperty 挂到 UClass
    GeneratorFunction: NewObject<UFunction>，造参数/返回值 FProperty，
                       Bind() + StaticLink() + AddFunctionToFunctionMap
    GeneratorInterface: Interfaces.Emplace(InterfaceClass, 0, true)
    ↓
EndGenerator (cpp:327-376)
    Bind() / StaticLink(true) / AssembleReferenceTokenStream()
    创建 CDO 并执行 ClassConstructor
    编辑器下 FBlueprintActionDatabase::RefreshClassActions → 立即出现在蓝图节点库
```

关键证据（已人工核验）：

```cpp:284:Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Dynamic/FDynamicClassGenerator.cpp
void FDynamicClassGenerator::BeginGenerator(UClass* InClass, UClass* InParentClass)
{
	InClass->SetSuperStruct(InParentClass);
	InClass->ClassFlags |= CLASS_Native;      // ← 伪装成原生 C++ 类
	InClass->ClassConstructor = &FDynamicClassGenerator::ClassConstructor;
}
```

自定义 `ClassConstructor`（cpp:684-837）在实例化时：初始化动态属性默认值、导入 `[DefaultValue]`、处理 `[DefaultSubObject]`/`[RootComponent]`（为 Actor 动态构建 `USimpleConstructionScript`/`USCS_Node`，即 C# 里也能像蓝图那样挂组件），最后回调 `OnPostClassConstructor` 创建 C# wrapper 并调用 C# 构造函数。

UStruct/UEnum/UInterface 分别由 `FDynamicStructGenerator` / `FDynamicEnumGenerator` / `FDynamicInterfaceGenerator` 以相同模式生成。

### 2.4 从启动到可用的完整时序

1. **引擎启动**：`UnrealCSharpCore` 模块激活 → 创建脚本域（CoreCLR 或 Mono）→ 加载 `Script/UE` 绑定程序集和用户 Script 程序集（编辑器下由 Compiler 模块先用 Roslyn 编译）。
2. **生成触发**（三个时机）：
   - 编辑器启动：`FUnrealCSharpEditorModule::StartupModule` → `FDynamicGenerator::Generator()`
   - 运行时：`FUnrealCSharpModule` 模块激活回调
   - C# 热重载后：`FCSharpCompilerRunnable` 编译完成回调 / 文件级增量（依据 CodeAnalysis 生成的 `Dynamic.json` 映射文件）
3. **扫描注册**：`FDynamicGenerator::Generator()` 按 **Enum → Struct → Interface → Class** 顺序，遍历程序集，`Utils.GetTypesWithAttribute` 找到所有命中类型。
4. **拓扑生成**：依赖图按序执行上面的构造流程。
5. **逻辑绑定**：`FDynamicRegistry::OnCSharpEnvironmentInitialize` 对每个动态类执行 `FCSharpBind::Bind`，把 UFunction 的 NativeFunc 换成 `execCallCSharp`（见第 5 节）。
6. 此后该 UClass 与原生 C++ 类在引擎内**无本质区别**：可 Spawn、可被蓝图继承、可序列化、出现在父类选择器里。

---

## 3. 蓝图继承 C# 动态类

UE 限制蓝图只能继承满足以下条件的类：有 `CLASS_Native` 标志且 `ClassGeneratedBy == nullptr`（即"原生 C++ 类"）。插件的解法非常直接——**伪造这两个条件**：

1. `BeginGenerator` 里 `ClassFlags |= CLASS_Native`（见上节代码）；
2. 动态类用 `NewObject<UClass>` 构造，`ClassGeneratedBy` 天然为 nullptr。

于是：

- **新建蓝图对话框的父类选择器**（标准 UE Class Viewer）会把这个动态类当作 C++ 类放行，用户可以直接选它做蓝图父类；
- **新建 C# 类对话框**（插件自己的 `UnrealCSharpEditor/Source/.../ClassCollector.cpp`）同样靠 `IsValidParentClass` 检查 `CLASS_Native` 通过；
- 类生成完毕后 `EndGenerator` 刷新 `FBlueprintActionDatabase`，蓝图编辑器里立即能搜到新类/新节点。

**蓝图子类重写 C# 函数**时不需要任何特殊处理：子蓝图 BPGC 自己的同名 UFunction（脚本字节码）按 UE 正常虚函数链覆盖，天然生效。

**一个修补细节**：蓝图每次重编译会把 BPGC 的 `ClassConstructor` 覆盖回引擎默认值，所以插件在 `OnPrePIEEnded`（FDynamicClassGenerator.cpp:114-133）等时机把所有继承了动态类的 BPGC 的 `ClassConstructor` 重设回 `FDynamicClassGenerator::ClassConstructor`。

### 3.1 C# 动态类变更时的 Reinstance（热重载）

文件：`FDynamicClassGenerator.cpp:435-544`

```
旧类 Rename 成 DYNAMIC_REINSTANCE + CLASS_NewerVersionExists
    ↓
FBlueprintCompileReinstancer::ReplaceInstancesOfClass   // 旧实例全部换成新类
    ↓
找出所有 IsChildOf(Old) 的子蓝图（BPGC）
    ↓
Blueprint->ParentClass = NewClass                       // 重新挂父类
RefreshAllNodes + 修正引用旧类的 Pin
    ↓
FKismetEditorUtilities::CompileBlueprint               // 重编译所有子蓝图
    ↓
旧类/旧 Blueprint RemoveFromRoot + MarkAsGarbage
```

配合 `DynamicBlueprintExtension`（`bIsFullCompile = false`）避免全量重编译，保证编辑器下改 C# 热重载后蓝图子类依然正确联动。

---

## 4. C# 继承蓝图类

反向继承通过**生成绑定代码 + 命名约定**实现，分三步：

### 4.1 蓝图资产导出为 C# 绑定类

插件编辑器模块（`Source/ScriptCodeGenerator/Private/FAssetGenerator.cpp`）监听资产增删改，为每个蓝图资产生成带 `[PathName]` 的 C# 绑定文件，输出到项目的 `Script/Game/Proxy/` 目录：

```csharp
// Script/Game/Proxy/.../BP_TestClass_C.cs（自动生成）
[PathName("/Game/UnitTest/Core/BP_TestClass.BP_TestClass_C")]
public partial class BP_TestClass_C : AActor, IStaticClass
{
    // 蓝图里定义的函数/属性/事件签名，供 C# 侧强类型访问
}
```

`[PathName]` 记录了蓝图资产的真实路径，C++ 侧 `FClassReflection` 会读取并存下 `PathName`/`Parent`（`FClassReflection.cpp:206-211`）。

### 4.2 用户 C# 类继承该绑定类（命名约定：必须 `_C` 结尾）

```csharp
// 用户代码
[UClass]
public partial class CSharp_TestActor_C : BP_TestClass_C   // ← 继承蓝图！
{
}
```

### 4.3 运行时解析父类 + 伪造蓝图链

`FDynamicClassGenerator` 处理该类时（FDynamicClassGenerator.cpp:162-213）：

1. 用 `LoadClass<UObject>(nullptr, *Parent->GetPathName())` 按路径加载父类的真实 `UBlueprintGeneratedClass`；
2. 检测类名 `_C` 后缀（`IsDynamicBlueprintGeneratedClass`，cpp:839-842），走 `GeneratorBlueprintGeneratedClass`（cpp:409-432）：**伪造一整套蓝图对象**——`NewObject<UBlueprintGeneratedClass>` + `NewObject<UBlueprint>`，并设置 `Blueprint->SkeletonGeneratedClass = Blueprint->GeneratedClass = Class; Class->ClassGeneratedBy = Blueprint;`

为什么必须伪造？因为 UE 不允许原生 UClass（带 `CLASS_Native`）以 BPGC 为父类。所以这条分支的 `BeginGenerator(UBlueprintGeneratedClass*)`（cpp:305-314）在复用 UClass 版逻辑后**额外清掉 `CLASS_Native`** 并设置 `Cast<UBlueprint>(ClassGeneratedBy)->ParentClass = InParentClass`，让整条继承链在 UE 类型系统眼里"合法"。

### 4.4 另一种模式：[Override] partial 覆写已有蓝图（不创建新类）

除 `[UClass]` 动态类外，还有一种更轻量的"bind 蓝图并替换方法"模式——**不新建任何 UClass，直接把 C# 方法嫁接到已存在的 BPGC 上**：

```csharp
// 用户手写：Script/Game/UnrealCSharpTest/MyTest/CSharpTest_C.cs
[Override]                                    // 注意：不是 [UClass]！
public partial class CSharpTest_C             // 与 Proxy 生成类同名同命名空间
{
    [Override]
    public override void ReceiveBeginPlay()
    {
        base.ReceiveBeginPlay();
        UKismetSystemLibrary.PrintString(this, "Hello My World");
    }
}
```

C# 编译时，它和自动生成的 `Script/Game/Proxy/.../CSharpTest_C.cs`（`[PathName("/Game/MyTest.CSharpTest_C")]`）**partial 合并成一个类**，该类对应已存在的蓝图 BPGC。实现分两期：

1. **编译期（编辑器）**：`Script/CodeAnalysis/CodeAnalysis.cs`（L243-246）用 Roslyn 扫出 `[Override]` 方法，写入 `OverrideFunction.json`（`{"命名空间.类名": ["ReceiveBeginPlay", ...]}`）；`FClassGenerator.cpp:72/289-298` 生成 Proxy 时读取该 json，为被覆写的函数额外生成 `Xxx__Override`（`new`）方法——调用蓝图原 K2 逻辑的入口（受 `EnableCallOverrideFunction` 设置控制）。
2. **运行期（全平台，编辑器/打包/手机行为一致）**：BPGC 创建时经 `GUObjectArray` 全局监听器触发 `FCSharpBind::Bind` → 按**函数名 + 参数个数**匹配 `FUNC_BlueprintEvent && !FUNC_Final` 函数 ↔ C# `[Override]` 方法 → 先 `DuplicateFunction` 复制 `Xxx__Override` 副本保底，再偷换原函数指针。完整触发链与替换代码见 **5.1 节**（该节的"绑定触发链"即由此模式的运行期组成）。

**替换效果与引擎依据**：UE 的函数分发是 **native XOR 字节码** 的排他结构——`ProcessEvent` / 蓝图字节码的 `CallFunction` 指令，只要 UFunction 带 `FUNC_Native` 就直接 `Function->Invoke(...)`（调 `NativeFunc` 指针），完全不碰 `Script` 字节码（见引擎 `ScriptCore.cpp` 的 FUNC_Native 分支）。因此 `SetNativeFunc(execCallCSharp)` + `|= FUNC_Native` 两行合谋后，蓝图原字节码从**每个调用入口**都被截断（数据仍在内存，只是永不执行）。三个精度：

- **截断粒度 = 整个函数体**：事件图所有节点（打印、延时、调用其它函数）随整个函数一起失效；
- **函数级而非类级**：未被 `[Override]` 的蓝图函数照常跑字节码；
- **逃生通道**：`DuplicateFunction` 在偷换**之前**执行，副本保留原始 flags + 原始字节码（无 `FUNC_Native`，仍走字节码分支）。Proxy 生成的 `Xxx__Override` 方法经 `__Xxx__Override` 静态字段的 hash 调到该副本，即调回蓝图原逻辑；`base.Xxx()` 则走 C# 继承链调父类（如 AActor）的原生版本；`FCSharpFunctionRegister` 存档替换前的 flags 与指针，可恢复。

### 4.5 蓝图资产变化时的联动

`FEditorListener::OnAssetUpdatedOnDisk` → `FAssetGenerator` 重新生成该蓝图的 C# Proxy 绑定文件 → C# 重编译 → 触发动态类重新生成 + 第 3.1 节的 Reinstance 链。`GEditor->OnBlueprintCompiled` 另触发 ClassCollector 刷新父类层级列表。

---

## 5. 函数调用链：双向分发

### 5.1 UE → C#（Override / 事件）：共享 thunk `execCallCSharp`

**不是每个函数生成一个 thunk，所有函数共用一个**：

```cpp:5:Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Reflection/Function/CSharpFunction.cpp
DEFINE_FUNCTION(UCSharpFunction::execCallCSharp)
{
    // 按当前正在执行的 UFunction 的 hash 查描述符
    if (auto FunctionDescriptor = FCSharpEnvironment::GetEnvironment()
        .GetOrAddFunctionDescriptor<FCSharpFunctionDescriptor>(
            GetTypeHash(Stack.CurrentNativeFunction)))
        FunctionDescriptor->CallCSharp(Context, Stack, RESULT_PARAM);
}
```

**绑定触发链（重点：纯运行时、无编辑器依赖、全平台一致）**

`SetNativeFunc` 偷换不是编译期/导入期的动作，而是**对象创建时由引擎级全局监听器实时触发**的：

```
FUObjectListener::OnUnrealCSharpModuleActive           (FUObjectListener.cpp:27-32)
    GUObjectArray.AddUObjectCreateListener(this)       ← UE 全局对象数组监听，引擎机制，非编辑器专属
        ↓ 任意 UObject 创建（含从 pak 加载的 BPGC）
FCSharpEnvironment::NotifyUObjectCreated               (FCSharpEnvironment.cpp:240-264)
    ├─ 游戏线程对象 → 立即 FCSharpBind::Bind<true>
    └─ 非游戏线程（异步加载，如手机 pak 中的 BPGC）→ 进 AsyncLoadingObjectArray
       加锁暂存，稍后游戏线程 flush 时补 Bind（FCSharpEnvironment.cpp:368-373）
        ↓
FCSharpBind::Bind → 匹配 FUNC_BlueprintEvent && !FUNC_Final 函数 ↔ C# [Override] 方法
        ↓
UFunction::SetNativeFunc(UCSharpFunction::execCallCSharp)
```

三个关键事实：

1. **监听器挂在 `GUObjectArray` 上**——这是 UE 引擎的全局对象数组（每个 UObject 创建/删除必经），与编辑器无关；
2. **整条链全部位于 `UnrealCSharp`/`UnrealCSharpCore` 运行时模块，没有任何 `WITH_EDITOR` 条件编译**——编辑器、PIE、打包后的桌面版、手机版行为完全一致；
3. **异步加载有兜底**：手机上 pak 里的 BPGC 通常在异步加载线程创建，`NotifyUObjectCreated` 会将其暂存进 `AsyncLoadingObjectArray`（临界区保护），回到游戏线程 flush 时再执行 Bind——所以无论对象在哪个线程被加载，函数指针替换最终都会发生。

（打包/移动端的其余环境差异见第 10 节。）

绑定发生在 `FCSharpBind::BindImplementation`（`FCSharpBind.cpp:311-394`），分两种情况（已人工核验）：

```cpp:368:Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Registry/FCSharpBind.cpp
// 情况 A：函数声明在本类（C# 新增的 [UFunction]）
OriginalFunction->SetNativeFunc(UCSharpFunction::execCallCSharp);  // 偷换原生指针
OriginalFunction->FunctionFlags |= FUNC_Native;

// 情况 B：函数声明在父类（C# 覆写蓝图/原生父类的函数）→ 先复制一份再接管
NewFunction = DuplicateFunction(OriginalFunction, InClass, FunctionName);
NewFunction->SetNativeFunc(UCSharpFunction::execCallCSharp);
```

`DuplicateFunction`（cpp:474-518）复制出的 `Xxx__Override` 副本保留原函数的 native 指针与原始 flags（`FCSharpFunctionRegister` 存档）；`EnableCallOverrideFunction` 开启时，副本的 hash 会写入 C# 类的 `__Xxx__Override` 静态字段（`FCSharpBind.cpp:346-366`），Proxy 生成的 `Xxx__Override` 方法由此调回蓝图原 K2 逻辑——即 4.4 节所说的"逃生通道"。而 `base.Xxx()` 走 C# 继承链调父类（如 AActor）版本的原生函数。

`FCSharpFunctionDescriptor::CallCSharp`（`FCSharpFunctionDescriptor.cpp:26-142`）：从 `FFrame/Stack` 手动逐参数解包（含 out/ref 参数构造 `FOutParmRec`），然后 `Invoke(Method, 托管对象, ...)` 调进 CLR。

### 5.2 C# → UE（调用 UFunction / 原生 API）

C# 侧入口 `Script/UE/Library/FFunctionImplementation.cs`，按「有无返回值/入参/出参/POD」分为 GenericCall/PrimitiveCall/CompoundCall 编号的模板族：

```csharp
// 通过 MethodBridge 查回 C++ 原生函数指针，转成非托管函数指针直接调用
__FFunction_GenericCall0Implementation =
    (delegate* unmanaged[CDecl]<nint, uint, void>)
    MethodBridge.GetMethod("Script.Library.FFunctionImplementation::FFunction_GenericCall0Implementation");
```

C++ 侧（`Domain/Interop/FRegisterFunction.cpp`）：**handle → UObject，hash → UFunction 描述符，然后 `UObject::ProcessEvent`**。

### 5.3 C# 重写蓝图函数 / 蓝图重写 C# 函数（前文机制汇总）

| 方向 | 机制 |
|---|---|
| C# 重写蓝图父类函数 | 情况 B：`DuplicateFunction` 到 C# 动态类 + `SetNativeFunc(execCallCSharp)`，按 UE 正常函数查找链生效 |
| 蓝图子类重写 C# 函数 | 子蓝图自己的 BPGC UFunction（字节码）按正常虚函数链覆盖，无需插件干预 |

---

## 6. 属性读写与对象模型

### 6.1 属性链路

- C# → C++：`Script/UE/Library/FPropertyImplementation.cs`，同样 `delegate* unmanaged[Cdecl]` + `(handle, propertyHash, buffer*)` 签名。
- C++ 侧：`FRegisterProperty.cpp` → handle 查对象地址，propertyHash 查 `FPropertyDescriptor`（虚函数 `Get/Set/CopyValue/Identical`，按属性类型派生约 30 个子类：FObject/FStr/FStruct/FArray/FMap/FBool...Descriptor）。
- 动态类自己的属性由 `FTypeBridge::Factory` 直接 `new FFloatProperty(...)`/`new FMapProperty(...)` 构造（`Public/Bridge/FTypeBridge.inl`），容器属性递归构造 Key/Value/Inner。

### 6.2 UObject 包装与 GC（双向弱引用）

- **C# 侧**：`Script/UE/CoreUObject/Object.cs` 的 `UObject` 是 wrapper，只持有 `nint GarbageCollectionHandle`（不持裸指针）；`Script/Interop/Handle/HandleData.cs` 维护 `Dictionary<nint, GCHandle>` 表 + 引用计数。
- **C++ 侧**：`FObjectRegistry` 用 `TWeakObjectPtr<const UObject>` 做双向映射表。
- **结论**：两边互相持弱引用，**UObject 生命周期完全由 UE GC 决定**，C# wrapper 生命周期由 GCHandle 决定，二者通过 `FUObjectCreateListener/FUObjectDeleteListener` 同步。C# 想保活一个 UObject 需显式 `AddToRoot()`/`AddReference()`。

---

## 7. .NET 运行时加载与互操作形式

文件：`Source/UnrealCSharpCore/Private/Domain/`

- **双后端**：`FScriptDomainFactory` 按设置返回 `FCoreCLRDomain` 或 `FMonoDomain`。
- **CoreCLR 模式**：加载 `hostfxr.dll` → `hostfxr_initialize_for_runtime_config` → `hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer)` 建立程序集加载能力。
- **调用接口形式**：不是 P/Invoke，而是**双向原生函数指针表**：
  - C++ → C#：`FCoreCLRDomain::RegisterInterop()` 用 `load_assembly_and_get_function_pointer` 取回 Interop 程序集中各 Bridge 类（AssemblyLoader/MethodBridge/StringBridge...）的 `UnmanagedCallersOnly` 静态函数指针；调用 C# 方法时经 `MethodBridge::Invoke` → `System.Reflection MethodBase.Invoke` 反射调用。
  - C# → C++：C++ 把一批原生实现（如 `FFunction_GenericCall0Implementation`）通过 `MethodBridge::RegisterBinding` 塞给 C#，C# 用 `MethodBridge.GetMethod("全名")` 查回并转 `delegate* unmanaged` 直接调用。
  - Mono 模式则用 `[MethodImpl(MethodImplOptions.InternalCall)] extern`（mono 内建 icall）。

---

## 8. Template 目录说明

| 文件 | 用途 |
|---|---|
| `Template/Dynamic/DynamicActor.cs` 等 | 新建动态类的脚手架示例：演示 `[UClass]` 声明 + `[Override]` 覆写 `ReceiveBeginPlay`/`ReceiveEndPlay`（蓝图事件） |
| `Template/Override/Actor.cs` 等 | 覆写原生引擎类的基线模板 |
| `Template/Game.csproj` 等 | 用户 Script 项目的 csproj/props 模板 |

---

## 9. 关键文件索引

| 主题 | 文件 |
|---|---|
| C# 特性定义 | `Plugins/UnrealCSharp/Script/UE/Dynamic/**`（250+ 特性） |
| C# 反射助手 | `Plugins/UnrealCSharp/Script/UE/CoreUObject/Utils.cs` |
| 类型注册 | `Source/UnrealCSharpCore/Public/Reflection/FReflectionRegistry.h`、`FClassReflection.h` |
| **UClass 动态构造** | `Source/UnrealCSharpCore/Private/Dynamic/FDynamicClassGenerator.cpp`（CLASS_Native 伪装：L298；伪造 BPGC：L409；Reinstance：L435） |
| 属性/函数构造 | `Source/UnrealCSharpCore/Private/Dynamic/FDynamicGeneratorCore.cpp`、`Public/Bridge/FTypeBridge.inl` |
| 依赖拓扑排序 | `Source/UnrealCSharpCore/Private/Dynamic/FDynamicDependencyGraph.cpp` |
| **函数接管** | `Source/UnrealCSharp/Private/Registry/FCSharpBind.cpp`（SetNativeFunc：L368/388）、`Reflection/Function/CSharpFunction.cpp`（execCallCSharp） |
| 蓝图→C# 绑定生成 | `Source/ScriptCodeGenerator/Private/FAssetGenerator.cpp`、`FClassGenerator.cpp` |
| 编辑器父类列表 | `Source/UnrealCSharpEditor/Private/NewClass/ClassCollector.cpp` |
| CoreCLR 宿主 | `Source/UnrealCSharpCore/Private/Domain/CoreCLR/FCoreCLRDomain.cpp` |
| C# 互操作层 | `Plugins/UnrealCSharp/Script/Interop/`（HandleData/MethodBridge 等 10 个文件） |
| 热重载编译器 | `Source/Compiler/`（Roslyn）+ `Script/CodeAnalysis/CodeAnalysis.cs`（产出 Dynamic.json） |
| 委托/事件 | `Source/UnrealCSharp/Private/Reflection/Delegate/`（DelegateHandler / MulticastDelegateHandler） |
| 网络复制/RPC | `UnrealCSharpCore/Private/Dynamic/FDynamicGeneratorCore.cpp:366-698`（特性→Net 标志） |
| 动态接口 | `UnrealCSharpCore/Private/Dynamic/FDynamicInterfaceGenerator.cpp` |
| 引擎绑定批量生成入口 | `UnrealCSharpEditor/Private/UnrealCSharpEditor.cpp:237-297`（FClassGenerator 等） |

---

## 10. 打包与移动端行为（Android/iOS）

**核心结论：`[Override]` 替换、动态类生成等机制在打包后完全一样**——触发链与替换逻辑全部是纯运行时代码，无任何 `WITH_EDITOR` 门槛（细节见 5.1 节"绑定触发链"）；手机 pak 中即使走异步加载线程的 BPGC，也有暂存 + 游戏线程 flush 兜底。差异仅在环境层面：

### 10.1 编辑器做的事变成"提前做完"

Proxy 代码生成、Roslyn CodeAnalysis（`OverrideFunction.json`）、C# 编译——全部是**编辑器编译期**工作，产物是 `Game.dll`/`UE.dll`/`Interop.dll`。设备上不存在任何代码生成，只加载现成 dll。

### 10.2 C# dll 从 pak 的 Content 目录加载

`UUnrealCSharpSetting` 的 `PublishDirectory` 是 `FGameContentDirectoryPath` 类型（`UnrealCSharpSetting.cpp:11`），即 dll 被发布到 `Content/` 下的子目录，随 Content 普通文件一起打进 pak。设备上 `FMonoFunctionLibrary::GetAssemblyPath`（`FMonoFunctionLibrary.cpp:71-77`）从 `ProjectContentDir()/PublishDirectory` 加载——这就是插件宣称"支持 Pak 热更新 C#"的原理：**C# dll 是普通 Content 资源，换 pak 即可更新游戏逻辑**。

### 10.3 移动平台默认 Mono 而非 CoreCLR

```cpp:17:Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Setting/UnrealCSharpSetting.cpp
WindowsScriptDomainType(EScriptDomainType::CoreCLR),
LinuxScriptDomainType(EScriptDomainType::CoreCLR),
MacScriptDomainType(EScriptDomainType::CoreCLR),
AndroidScriptDomainType(EScriptDomainType::Mono),   // ← 手机默认 Mono
IOSScriptDomainType(EScriptDomainType::Mono),
```

- 桌面平台：CoreCLR（hostfxr + `UnmanagedCallersOnly` + `delegate* unmanaged` 双向函数指针桥）。
- 移动平台：内嵌 Mono 运行时，C#→C++ 用 `[MethodImpl(MethodImplOptions.InternalCall)]`（mono icall）。
- 此差异只影响互操作底层通道；`execCallCSharp` thunk、`SetNativeFunc` 偷换、hash 分发等上层机制与后端无关。

### 10.4 打包下动态类多一步注册、少了编辑器功能

- `#if !WITH_EDITOR` 下，动态类生成完毕后额外调用 `NotifyRegistrationEvent(NRT_Class/NRT_ClassCDO)`（`FDynamicClassGenerator.cpp:356-375`）——用 UE 官方运行时加载注册接口把运行时构造的 UClass 登记进 UObject 系统（打包下没有编辑器那套注册路径）。
- `FBlueprintActionDatabase` 刷新、热重载、Reinstance、子蓝图自动重编译——全是编辑器专属，设备上不存在也不需要（dll 已是最终形态）。

### 10.5 编辑器 vs 手机行为对照

| | 编辑器 | 手机（打包） |
|---|---|---|
| `[Override]` 替换（SetNativeFunc + execCallCSharp） | ✅ | ✅ 完全相同 |
| 动态类运行时生成（[UClass]） | ✅ | ✅（多一步 NotifyRegistrationEvent） |
| 代码生成 / Roslyn 编译 | ✅ 实时 | ❌（编辑器期已完成，dll 进 pak） |
| .NET 后端 | CoreCLR | Mono |
| 热重载 / Reinstance | ✅ | ❌（换 pak 更新） |

---

## 11. 其他重要机制（补充分析）

### 11.1 委托/事件绑定：中介 UObject 模式

C# 不能把方法指针直接塞进 UE 的 `FScriptDelegate`（引擎只认 `UObject* + FName`）。插件的解法是**插入一个 AddToRoot 的中介 UObject**：

```
C#: OnDeath.Add(方法) / += 方法
    ↓ FRegisterMulticastDelegate
UMulticastDelegateHandler（AddToRoot 的中介 UObject）
    ScriptDelegate.BindUFunction(this, "CSharpCallBack")   // 中介把自己绑进真多播委托
    (UObject, Method) 对存入 DelegateWrappers 数组
    ↓ 引擎广播时
UMulticastDelegateHandler::ProcessEvent 命中 "CSharpCallBack"
    → 遍历 DelegateWrappers 逐个 FCSharpDelegateDescriptor::CallDelegate 回调 C# 方法
```

文件：`Source/UnrealCSharp/Private/Reflection/Delegate/MulticastDelegateHandler.cpp`（`Add`：L75-90，`ProcessEvent`：L5-21）；单播同理 `DelegateHandler.cpp`。输入绑定（BindAction）见 `Script/UE/CoreUObject/EnhancedInputComponent.cs`——按方法名 `BindUFunction` 到动态类的 UFunction，经 `execCallCSharp` 回调（即复用 5.1 的函数接管链）。

### 11.2 网络复制与 RPC：`_Implementation` 命名约定

- **属性复制**：`[Replicated]` → `CPF_Net` + LifetimeCondition；`[ReplicatedUsing]` → `CPF_Net|CPF_RepNotify`（`FDynamicGeneratorCore.cpp:366-389`），并登记进 BPGC 的复制数组（`FDynamicClassGenerator.cpp:550-556`）。
- **RPC**：`[Server]`/`[Client]`/`[NetMulticast]`/`[Reliable]`/`[WithValidation]` 特性 → 对应 `FUNC_Net*` 标志，且复刻了 UHT 的 reliable/unreliable 校验规则（`FDynamicGeneratorCore.cpp:550-698`）。
- **命名约定**：C# 侧按 UE 惯例把 RPC 实现写成 `XXX_Implementation`——`FCSharpBind.cpp:251-259` 扫函数时把 `FUNC_Net` 函数的匹配名编码为 `Name_Implementation`，`FCSharpFunctionDescriptor.cpp:14-21` 调用时按该后缀查找并接管，引擎网络代码收包后调 `_Implementation` thunk 即直接进 C#。

### 11.3 UInterface 的实现与调用

- `[UInterface]` 动态接口由 `FDynamicInterfaceGenerator` 用 `CLASS_Interface` 标志生成接口 UClass 与签名函数；C# 类实现接口时 `InClass->Interfaces.Emplace(InterfaceClass, 0, true)` 挂到动态 UClass（`FDynamicClassGenerator.cpp:661-682`）。
- 调用链：C++/蓝图 `Execute_接口函数` → ProcessEvent 找到实现类上的同名 UFunction → `execCallCSharp` → C# 实现（复用 5.1 的接管链，无需新机制）。
- C# 侧持接口用 `TScriptInterface`（`Script/UE/CoreUObject/TScriptInterface.cs`），封送在 `FInterfacePropertyDescriptor.cpp` / `FRegisterScriptInterface.cpp`。接口变更会 ReInstance 实现它的蓝图（`FDynamicInterfaceGenerator.cpp:271-312`）。

### 11.4 C# 异常的边界行为（重要警示）

- **Mono 后端（手机/移动）**：所有 `mono_runtime_invoke` 走带 out-exc 的重载，有异常则 `mono_unhandled_exception`（`FMonoDomain.cpp:620-684`），C# 异步（SynchronizationContext tick）抛出的异常也转发到同一出口（L147-153）——即统一走"未处理异常"流程（崩溃日志）。
- **CoreCLR 后端（桌面）**：`Domain/CoreCLR` 下**没有任何异常拦截代码**——C# 异常会以原生 SEH 形式直接穿出调用边界，插件不做防护。
- C++ 侧没有 try/catch 包装（UnrealCSharp 模块全目录 `catch` 出现为 0）。**结论：不要指望跨边界异常被吞掉，C# 回调里必须自行 try/catch。**

### 11.5 UObject 失效语义（重要警示）

UObject 被 UE GC 删除后：`NotifyUObjectDeleted` → `RemoveObjectReference`（`FCSharpEnvironment.cpp:266-285`）——从双向映射表移除并释放 C# 端 GCHandle（`HandleData.cs:120-155`）。但：

- C# wrapper 对象**不会被置空、也不抛异常**——它仍持有旧的 `GarbageCollectionHandle`；
- 再调它的方法时，C++ 侧 `GetObject(handle)` 返回空，interop 函数**静默早退**（如 `FRegisterObject.cpp:39-66` 的 if 包裹），wrapper 由 C# GC 自然回收；
- **因此引用了 UObject 的 C# 代码必须在调用前用 `UObject.IsValid()`（`Object.cs:37`）判断存活**，否则是无声失败（拿不到返回值/不执行，也不报错）。

### 11.6 Script/UE 与 Script.Engine 的来源区别（易误解）

- `Script/UE/`（325 个文件：CoreUObject/Library/Reflection 等）是**手写的 C# 基础运行库**：UObject 基类、TArray/TMap/TSubclassOf 容器、FFunctionImplementation 等 interop 实现、全部 UHT 特性类——不是生成的。
- `Script/Engine`、`Script/Game` 等模块绑定是**编辑器下批量生成**的：入口 `FUnrealCSharpEditorModule::Generator()`（`UnrealCSharpEditor.cpp:237-297`）依次跑 FClassGenerator（遍历全部 UClass）/FStructGenerator/FEnumGenerator/FAssetGenerator 等，触发时机为编辑器工具栏按钮、Commandlet（`UnrealCSharpGeneratorCommandlet.cpp`）、资产监听。`Source/SourceCodeGenerator` 则是 UBT 插件（csproj 方案文件生成）。

---

## 12. 测试用例参考

项目中自带覆盖各种继承组合的单元测试，可直接阅读学习：

- `Script/Game/UnrealCSharpTest/UnitTest/Dynamic/BlueprintXxx*`（蓝图继承 C# 各场景）
- `Script/Game/UnrealCSharpTest/UnitTest/Dynamic/XxxBlueprint*`（C# 继承蓝图各场景）
- `Script/Game/Proxy/UnrealCSharpTest/UnitTest/Core/BP_TestClass_C.cs`（自动生成的蓝图绑定示例）
