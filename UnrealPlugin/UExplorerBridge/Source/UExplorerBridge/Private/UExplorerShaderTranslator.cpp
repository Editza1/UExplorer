#include "UExplorerShaderTranslator.h"
#include "UExplorerAssetImporter.h"
#include "AssetToolsModule.h"
#include "IAssetTools.h"
#include "Factories/MaterialFactoryNew.h"
#include "MaterialEditingLibrary.h"
#include "Materials/Material.h"
#include "Materials/MaterialParameterCollection.h"
#include "Materials/MaterialExpressionCollectionParameter.h"
#include "Materials/MaterialExpressionAdd.h"
#include "Materials/MaterialExpressionSubtract.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionDivide.h"
#include "Materials/MaterialExpressionPower.h"
#include "Materials/MaterialExpressionSquareRoot.h"
#include "Materials/MaterialExpressionDotProduct.h"
#include "Materials/MaterialExpressionMin.h"
#include "Materials/MaterialExpressionMax.h"
#include "Materials/MaterialExpressionIf.h"
#include "Materials/MaterialExpressionSaturate.h"
#include "Materials/MaterialExpressionAbs.h"
#include "Materials/MaterialExpressionOneMinus.h"
#include "Materials/MaterialExpressionFrac.h"
#include "Materials/MaterialExpressionFloor.h"
#include "Materials/MaterialExpressionCeil.h"
#include "Materials/MaterialExpressionRound.h"
#include "Materials/MaterialExpressionLinearInterpolate.h"
#include "Materials/MaterialExpressionSine.h"
#include "Materials/MaterialExpressionCosine.h"
#include "Materials/MaterialExpressionConstant.h"
#include "Materials/MaterialExpressionConstant2Vector.h"
#include "Materials/MaterialExpressionConstant3Vector.h"
#include "Materials/MaterialExpressionConstant4Vector.h"
#include "Materials/MaterialExpressionScalarParameter.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionTextureCoordinate.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionAppendVector.h"
#include "Materials/MaterialExpressionParticleColor.h"
#include "Materials/MaterialExpressionDynamicParameter.h"
#include "Materials/MaterialExpressionTime.h"
#include "Materials/MaterialExpressionVertexColor.h"
#include "Materials/MaterialExpressionVertexNormalWS.h"
#include "Materials/MaterialExpressionVertexTangentWS.h"
#include "Materials/MaterialExpressionSceneColor.h"
#if !defined(UEXPLORER_HEADLESS)
#include "Materials/MaterialExpressionMaterialFunctionCall.h"
#include "Materials/MaterialExpressionFunctionInput.h"
#include "Materials/MaterialExpressionFunctionOutput.h"
#include "Materials/MaterialFunction.h"
#endif
#include "Materials/MaterialExpressionTransform.h"
#include "Materials/MaterialExpressionTwoSidedSign.h"
#include "Materials/MaterialExpressionLogarithm2.h"
#include "Materials/MaterialExpressionWorldPosition.h"
#include "Materials/MaterialExpressionObjectPositionWS.h"
#include "Materials/MaterialExpressionObjectRadius.h"
#include "Materials/MaterialExpressionCameraVectorWS.h"
#include "Materials/MaterialExpressionCameraPositionWS.h"
#include "Materials/MaterialExpressionPixelDepth.h"
#include "Materials/MaterialExpressionDepthFade.h"
#include "Engine/Texture2D.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "HAL/FileManager.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonReader.h"

DEFINE_LOG_CATEGORY_STATIC(LogUExplorerShaderTranslator, Log, All);

namespace
{
	struct FExprPin
	{
		UMaterialExpression* Expr = nullptr;
		FString OutputPin;
		int32 NumChannels = 1;
		int32 Depth = 0;
		bool bIsBoilerplate = false;
		bool bIsSelectionColor = false;

		bool IsValid() const { return Expr != nullptr && !bIsBoilerplate; }
	};

	enum class EInputSemantic : uint8
	{
		Unknown,
		TexCoord0,
		TexCoord2,
		TexCoord4,
		VertexColor,
		VertexNormalWS,
		VertexTangentWS,
		IsFrontFace,
		SvPosition,
		ParticleColor,
		DynamicParameter,
		Boilerplate
	};

	static void ConnectSmart(UMaterialExpression* From, const FString& FromPin, UMaterialExpression* To, const FString& ToPin)
	{
		if (!From || !To) return;
		if (UMaterialEditingLibrary::ConnectMaterialExpressions(From, FromPin, To, ToPin)) return;
		if (!FromPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialExpressions(From, TEXT(""), To, ToPin)) return;
		if (ToPin.IsEmpty())
		{
			UMaterialEditingLibrary::ConnectMaterialExpressions(From, TEXT(""), To, TEXT(""));
		}
	}

	static void ConnectToRootSmart(UMaterialExpression* From, const FString& FromPin, EMaterialProperty Prop)
	{
		if (!From) return;
		if (UMaterialEditingLibrary::ConnectMaterialProperty(From, FromPin, Prop)) return;
		if (!FromPin.IsEmpty())
			UMaterialEditingLibrary::ConnectMaterialProperty(From, TEXT(""), Prop);
	}

	static bool TryParseSingleLiteral(const FString& Op, float& OutVal)
	{
		FString Trim = Op.TrimStartAndEnd();
		if (!Trim.StartsWith(TEXT("l(")) || !Trim.EndsWith(TEXT(")"))) return false;
		FString Inner = Trim.Mid(2, Trim.Len() - 3);
		TArray<FString> Parts;
		Inner.ParseIntoArray(Parts, TEXT(","), true);
		if (Parts.Num() == 0) return false;
		OutVal = FCString::Atof(*Parts[0].TrimStartAndEnd());
		return true;
	}

	static void SplitRegSwizzle(const FString& Raw, FString& OutBase, FString& OutSwizzle)
	{
		int32 DotIdx = INDEX_NONE;
		if (Raw.FindLastChar(TEXT('.'), DotIdx))
		{
			OutBase = Raw.Left(DotIdx);
			OutSwizzle = Raw.Mid(DotIdx + 1);
		}
		else
		{
			OutBase = Raw;
			OutSwizzle.Empty();
		}
	}

	struct FDxbcInstruction
	{
		FString OpCode;
		bool bSaturate = false;
		bool bIsTex3D = false;
		TArray<FString> Args;
	};

	static bool ParseInstruction(const FString& Line, FDxbcInstruction& OutInst)
	{
		int32 SpaceIdx = INDEX_NONE;
		int32 OpParenDepth = 0;
		for (int32 i = 0; i < Line.Len(); ++i)
		{
			const TCHAR Ch = Line[i];
			if (Ch == TEXT('(')) OpParenDepth++;
			else if (Ch == TEXT(')')) OpParenDepth = FMath::Max(0, OpParenDepth - 1);
			else if (Ch == TEXT(' ') && OpParenDepth == 0) { SpaceIdx = i; break; }
		}

		if (SpaceIdx == INDEX_NONE)
		{
			OutInst.OpCode = Line.ToLower();
			return !OutInst.OpCode.IsEmpty();
		}

		OutInst.OpCode = Line.Left(SpaceIdx).ToLower();
		OutInst.bIsTex3D = OutInst.OpCode.Contains(TEXT("(texture3d)"));
		int32 ParenInOp = INDEX_NONE;
		if (OutInst.OpCode.FindChar(TEXT('('), ParenInOp))
			OutInst.OpCode = OutInst.OpCode.Left(ParenInOp);
		if (OutInst.OpCode.EndsWith(TEXT("_sat")))
		{
			OutInst.bSaturate = true;
			OutInst.OpCode.LeftChopInline(4);
		}

		FString ArgsStr = Line.Mid(SpaceIdx + 1);
		int32 ParenDepth = 0;
		int32 TokenStart = 0;
		for (int32 i = 0; i < ArgsStr.Len(); ++i)
		{
			const TCHAR Ch = ArgsStr[i];
			if (Ch == TEXT('(')) ParenDepth++;
			else if (Ch == TEXT(')')) ParenDepth--;
			else if (Ch == TEXT(',') && ParenDepth == 0)
			{
				OutInst.Args.Add(ArgsStr.Mid(TokenStart, i - TokenStart).TrimStartAndEnd());
				TokenStart = i + 1;
			}
		}
		OutInst.Args.Add(ArgsStr.Mid(TokenStart).TrimStartAndEnd());
		return true;
	}

	struct FDxbcTranslatorContext
	{
		UMaterial* Material = nullptr;
		TArray<TPair<FString, float>> ScalarParams;
		TArray<TPair<FString, FLinearColor>> VectorParams;
		TArray<TPair<FString, UTexture2D*>> OrderedTextures;
		TMap<FString, UTexture2D*> TextureByName;
		TMap<FString, FString> PsTextureSlots;
		TMap<FString, FString> VsTextureSlots;
		TMap<FString, FString> PsTextureParamNames;
		TMap<FString, FString> VsTextureParamNames;
		TArray<FLinearColor> EvaluatedCbSlots;
		TMap<FString, FString> CbSlotParamNames;
		UMaterialParameterCollection* PrimaryMpc = nullptr;
		TArray<UMaterialParameterCollection*> Mpcs;
		TMap<FString, int32> PsMpcCbSlots;
		TMap<FString, int32> VsMpcCbSlots;
		int32 CbVectorCount = 0;
		int32 PsMaterialCbSlot = -1;
		int32 PsViewCbSlot = -1;
		int32 PsMpcCbSlot = -1;
		int32 VsMaterialCbSlot = -1;
		int32 VsViewCbSlot = -1;
		int32 VsMpcCbSlot = -1;
		FString PsVertexFactory;

		// Input signature map parsed from DXBC comments (e.g. "v2" -> TexCoord0, "v3" -> ParticleColor, "v4" -> DynamicParameter)
		TMap<FString, EInputSemantic> InputSemantics;
		TMap<FString, int32> InputDynParamIndex;
		FString MaterialCbPrefix = TEXT("cb2");
		FString ViewCbPrefix = TEXT("cb0");
		FString MpcCbPrefix;
		FString NiagaraVfCbPrefix;

		enum class ECompareOp : uint8 { Ge, Lt, Eq, Ne };
		struct FCompareCond
		{
			FExprPin A;
			FExprPin B;
			ECompareOp Op = ECompareOp::Ge;
		};

		TMap<FString, FExprPin> RegValues;
		TMap<FString, FExprPin> ParamCache;
		TSet<FString> PowCondRegs;
		TMap<FString, FExprPin> PowLogBases;
		TMap<FString, FCompareCond> CompareConds;
		FString PixelDepthCondReg;
		FString SvPosReg;
		TSet<FString> SvPosRectRegs;
		TSet<FString> PixelDepthRegs;
		TSet<FString> SceneDepthRegs;
		TSet<FString> SceneDepthDiffRegs;
		TSet<FString> SceneColorUvRegs;
		TMap<FString, int32> PrimStructRowOffsets;
		TSet<FString> PrimIdRegs;
		bool bInVertexShader = false;
		UMaterialExpressionParticleColor* CachedParticleColor = nullptr;
		UMaterialExpressionDynamicParameter* CachedDynamicParam = nullptr;
		TMap<int32, UMaterialExpressionDynamicParameter*> CachedDynamicParams;
		TMap<int32, FExprPin> CachedTexCoords;
		UMaterialExpressionVertexColor* CachedVertexColor = nullptr;
		UMaterialExpressionVertexNormalWS* CachedVertexNormalWS = nullptr;
		UMaterialExpressionVertexTangentWS* CachedVertexTangentWS = nullptr;
		UMaterialExpressionTwoSidedSign* CachedTwoSidedSign = nullptr;
		UMaterialExpression* CachedScreenPos = nullptr;
		FExprPin CachedObjectPos;
		UMaterialExpressionWorldPosition* CachedWorldPos = nullptr;
		UMaterialExpressionCameraVectorWS* CachedCameraVec = nullptr;
		UMaterialExpressionTime* CachedTime = nullptr;
		FString TranslatedWorldPosReg;
		FString CameraVecRsqReg;
		FString CameraVecRsqBase;
		TCHAR CameraVecRsqLane = TEXT('w');
		FString CurrentDstMask;
		FString CurrentOpCode;
		int32 GraphYOffset = 0;
		int32 FirstMaterialTexSlot = 0;
		bool bSawSceneDepthSample = false;
		FExprPin CapturedOpacityPin;
		FExprPin CapturedOpacityMaskPin;
		FExprPin CapturedNormalPin;
		bool bIsDeferredGBuffer = false;

		void SetActiveInst(const FDxbcInstruction& ActiveInst)
		{
			CurrentOpCode = ActiveInst.OpCode;
			CurrentDstMask.Empty();
			if (ActiveInst.OpCode == TEXT("sincos") && ActiveInst.Args.Num() >= 2 &&
				ActiveInst.Args[0].Equals(TEXT("null"), ESearchCase::IgnoreCase))
			{
				FString TmpBase;
				SplitRegSwizzle(ActiveInst.Args[1], TmpBase, CurrentDstMask);
			}
			else if (ActiveInst.Args.Num() >= 1)
			{
				FString TmpBase;
				SplitRegSwizzle(ActiveInst.Args[0], TmpBase, CurrentDstMask);
			}
		}

		int32 NodeCounter = 0;
		TMap<int32, int32> DepthRowCounters;

		FDxbcTranslatorContext(UMaterial* InMat, const TSharedPtr<FJsonObject>& MatObj, const TMap<FString, UTexture2D*>& InTextures)
			: Material(InMat)
		{
			for (const auto& Pair : InTextures)
			{
				OrderedTextures.Add(TPair<FString, UTexture2D*>(Pair.Key, Pair.Value));
				TextureByName.Add(Pair.Key, Pair.Value);
			}

			if (MatObj.IsValid())
			{
				double NumVal = 0.0;
				if (MatObj->TryGetNumberField(TEXT("CbVectorCount"), NumVal))
					CbVectorCount = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("PsMaterialCbSlot"), NumVal))
					PsMaterialCbSlot = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("PsViewCbSlot"), NumVal))
					PsViewCbSlot = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("PsMpcCbSlot"), NumVal))
					PsMpcCbSlot = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("VsMaterialCbSlot"), NumVal))
					VsMaterialCbSlot = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("VsViewCbSlot"), NumVal))
					VsViewCbSlot = static_cast<int32>(NumVal);
				if (MatObj->TryGetNumberField(TEXT("VsMpcCbSlot"), NumVal))
					VsMpcCbSlot = static_cast<int32>(NumVal);
				MatObj->TryGetStringField(TEXT("PsVertexFactory"), PsVertexFactory);

				const TSharedPtr<FJsonObject>* TexSlotsObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("PsTextureSlots"), TexSlotsObj) && TexSlotsObj)
				{
					for (const auto& Pair : (*TexSlotsObj)->Values)
						PsTextureSlots.Add(FString(Pair.Key), Pair.Value->AsString());
				}
				const TSharedPtr<FJsonObject>* VsTexSlotsObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("VsTextureSlots"), VsTexSlotsObj) && VsTexSlotsObj)
				{
					for (const auto& Pair : (*VsTexSlotsObj)->Values)
						VsTextureSlots.Add(FString(Pair.Key), Pair.Value->AsString());
				}
				const TSharedPtr<FJsonObject>* PsTexParamNamesObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("PsTextureParamNames"), PsTexParamNamesObj) && PsTexParamNamesObj)
				{
					for (const auto& Pair : (*PsTexParamNamesObj)->Values)
						PsTextureParamNames.Add(FString(Pair.Key), Pair.Value->AsString());
				}
				const TSharedPtr<FJsonObject>* VsTexParamNamesObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("VsTextureParamNames"), VsTexParamNamesObj) && VsTexParamNamesObj)
				{
					for (const auto& Pair : (*VsTexParamNamesObj)->Values)
						VsTextureParamNames.Add(FString(Pair.Key), Pair.Value->AsString());
				}
				const TSharedPtr<FJsonObject>* PsMpcSlotsObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("PsMpcCbSlots"), PsMpcSlotsObj) && PsMpcSlotsObj)
				{
					for (const auto& Pair : (*PsMpcSlotsObj)->Values)
						PsMpcCbSlots.Add(FString(Pair.Key).ToLower(), static_cast<int32>(Pair.Value->AsNumber()));
				}
				const TSharedPtr<FJsonObject>* VsMpcSlotsObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("VsMpcCbSlots"), VsMpcSlotsObj) && VsMpcSlotsObj)
				{
					for (const auto& Pair : (*VsMpcSlotsObj)->Values)
						VsMpcCbSlots.Add(FString(Pair.Key).ToLower(), static_cast<int32>(Pair.Value->AsNumber()));
				}

				const TSharedPtr<FJsonObject>* ParamNamesObj = nullptr;
				if (MatObj->TryGetObjectField(TEXT("CbSlotParamNames"), ParamNamesObj) && ParamNamesObj)
				{
					for (const auto& Pair : (*ParamNamesObj)->Values)
						CbSlotParamNames.Add(FString(Pair.Key), Pair.Value->AsString());
				}

				const TArray<TSharedPtr<FJsonValue>>* MpcArr = nullptr;
				if (MatObj->TryGetArrayField(TEXT("ParameterCollections"), MpcArr) && MpcArr && MpcArr->Num() > 0)
				{
					for (const TSharedPtr<FJsonValue>& MpcVal : *MpcArr)
					{
						UMaterialParameterCollection* LoadedMpc = nullptr;
						if (const TSharedPtr<FJsonObject> MpcRef = MpcVal->AsObject(); MpcRef.IsValid())
						{
							FString MpcRawName;
							if (MpcRef->TryGetStringField(TEXT("Name"), MpcRawName) && !MpcRawName.IsEmpty())
							{
								const FString SanitizedMpc = FUExplorerAssetImporter::SanitizeAssetName(MpcRawName);
								const FString MpcAssetPath = FString::Printf(TEXT("/Game/UExplorer/MPC/%s.%s"), *SanitizedMpc, *SanitizedMpc);
								LoadedMpc = LoadObject<UMaterialParameterCollection>(nullptr, *MpcAssetPath);
#if defined(UEXPLORER_HEADLESS)
								if (!LoadedMpc)
								{
									FString MpcJsonPath;
									if (MpcRef->TryGetStringField(TEXT("MpcJson"), MpcJsonPath) && !MpcJsonPath.IsEmpty())
										LoadedMpc = FUExplorerAssetImporter::ImportMpcFromJson(MpcJsonPath);
								}
#endif
							}
						}
						Mpcs.Add(LoadedMpc);
					}
					for (UMaterialParameterCollection* M : Mpcs)
					{
						if (M) { PrimaryMpc = M; break; }
					}
				}

				const TArray<TSharedPtr<FJsonValue>>* EvalSlots = nullptr;
				if (MatObj->TryGetArrayField(TEXT("EvaluatedCbSlots"), EvalSlots) && EvalSlots)
				{
					for (const TSharedPtr<FJsonValue>& SV : *EvalSlots)
					{
						const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
						if (SV->TryGetArray(Arr) && Arr && Arr->Num() >= 4)
						{
							EvaluatedCbSlots.Add(FLinearColor(
								static_cast<float>((*Arr)[0]->AsNumber()),
								static_cast<float>((*Arr)[1]->AsNumber()),
								static_cast<float>((*Arr)[2]->AsNumber()),
								static_cast<float>((*Arr)[3]->AsNumber())));
						}
					}
				}

				const TArray<TSharedPtr<FJsonValue>>* OrderedScalars = nullptr;
				if (MatObj->TryGetArrayField(TEXT("OrderedScalarParams"), OrderedScalars) && OrderedScalars && OrderedScalars->Num() > 0)
				{
					for (const TSharedPtr<FJsonValue>& V : *OrderedScalars)
					{
						if (const TSharedPtr<FJsonObject> Obj = V->AsObject(); Obj.IsValid())
						{
							const FString Key = Obj->GetStringField(TEXT("Name"));
							const float Val = static_cast<float>(Obj->GetNumberField(TEXT("Value")));
							ScalarParams.Add(TPair<FString, float>(Key, Val));
						}
					}
				}
				else
				{
					const TSharedPtr<FJsonObject>* ScalarsObj = nullptr;
					if (MatObj->TryGetObjectField(TEXT("ScalarParameters"), ScalarsObj) && ScalarsObj)
					{
						for (const auto& Pair : (*ScalarsObj)->Values)
						{
							const FString Key(Pair.Key);
							if (Key.Equals(TEXT("RefractionDepthBias"), ESearchCase::IgnoreCase)) continue;
							ScalarParams.Add(TPair<FString, float>(Key, static_cast<float>(Pair.Value->AsNumber())));
						}
					}
				}

				const TArray<TSharedPtr<FJsonValue>>* OrderedVectors = nullptr;
				if (MatObj->TryGetArrayField(TEXT("OrderedVectorParams"), OrderedVectors) && OrderedVectors && OrderedVectors->Num() > 0)
				{
					for (const TSharedPtr<FJsonValue>& V : *OrderedVectors)
					{
						if (const TSharedPtr<FJsonObject> Obj = V->AsObject(); Obj.IsValid())
						{
							const FString Key = Obj->GetStringField(TEXT("Name"));
							const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
							if (Obj->TryGetArrayField(TEXT("Value"), Arr) && Arr && Arr->Num() >= 3)
							{
								const float R = static_cast<float>((*Arr)[0]->AsNumber());
								const float G = static_cast<float>((*Arr)[1]->AsNumber());
								const float B = static_cast<float>((*Arr)[2]->AsNumber());
								const float A = Arr->Num() >= 4 ? static_cast<float>((*Arr)[3]->AsNumber()) : 1.0f;
								VectorParams.Add(TPair<FString, FLinearColor>(Key, FLinearColor(R, G, B, A)));
							}
						}
					}
				}
				else
				{
					const TSharedPtr<FJsonObject>* VectorsObj = nullptr;
					if (MatObj->TryGetObjectField(TEXT("VectorParameters"), VectorsObj) && VectorsObj)
					{
						for (const auto& Pair : (*VectorsObj)->Values)
						{
							const FString Key(Pair.Key);
							if (Key.Equals(TEXT("SelectionColor"), ESearchCase::IgnoreCase)) continue;
							const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
							if (Pair.Value->TryGetArray(Arr) && Arr && Arr->Num() >= 3)
							{
								const float R = static_cast<float>((*Arr)[0]->AsNumber());
								const float G = static_cast<float>((*Arr)[1]->AsNumber());
								const float B = static_cast<float>((*Arr)[2]->AsNumber());
								const float A = Arr->Num() >= 4 ? static_cast<float>((*Arr)[3]->AsNumber()) : 1.0f;
								VectorParams.Add(TPair<FString, FLinearColor>(Key, FLinearColor(R, G, B, A)));
							}
						}
					}
				}
			}
		}

		template<typename TExpr>
		TExpr* Spawn(int32 Depth)
		{
			int32& Row = DepthRowCounters.FindOrAdd(Depth);
			const int32 PosX = -260 - Depth * 240;
			const int32 PosY = -140 + Row * 150 + GraphYOffset;
			Row++;
			NodeCounter++;
			return Cast<TExpr>(UMaterialEditingLibrary::CreateMaterialExpression(Material, TExpr::StaticClass(), PosX, PosY));
		}

		UMaterialExpression* SpawnByClassPath(const TCHAR* ClassPath, int32 Depth)
		{
			UClass* ExprClass = StaticLoadClass(UMaterialExpression::StaticClass(), nullptr, ClassPath);
			if (!ExprClass) return nullptr;
			int32& Row = DepthRowCounters.FindOrAdd(Depth);
			const int32 PosX = -260 - Depth * 240;
			const int32 PosY = -140 + Row * 150 + GraphYOffset;
			Row++;
			NodeCounter++;
			return UMaterialEditingLibrary::CreateMaterialExpression(Material, ExprClass, PosX, PosY);
		}

		FExprPin MakeConst(float Val, int32 Depth)
		{
			UMaterialExpressionConstant* C = Spawn<UMaterialExpressionConstant>(Depth);
			if (C) C->R = Val;
			return { C, TEXT(""), 1, Depth, false };
		}

		FExprPin MakeDemotedWS(UMaterialExpression* WsNode, int32 Depth)
		{
			UMaterialExpressionConstant3Vector* Zero3 = Spawn<UMaterialExpressionConstant3Vector>(Depth + 1);
			if (Zero3) Zero3->Constant = FLinearColor(0.0f, 0.0f, 0.0f, 1.0f);
			UMaterialExpressionSubtract* Demote = Spawn<UMaterialExpressionSubtract>(Depth);
			ConnectSmart(WsNode, TEXT(""), Demote, TEXT("A"));
			ConnectSmart(Zero3, TEXT(""), Demote, TEXT("B"));
			return { Demote, TEXT(""), 3, Depth, false };
		}

		static bool IsAllZeroLit(const FString& Op)
		{
			FString Trim = Op.TrimStartAndEnd();
			if (!Trim.StartsWith(TEXT("l(")) || !Trim.EndsWith(TEXT(")"))) return false;
			FString Inner = Trim.Mid(2, Trim.Len() - 3);
			TArray<FString> Parts;
			Inner.ParseIntoArray(Parts, TEXT(","), true);
			if (Parts.Num() == 0) return false;
			for (const FString& P : Parts)
			{
				if (!FMath::IsNearlyZero(FCString::Atof(*P.TrimStartAndEnd())))
					return false;
			}
			return true;
		}

		static TCHAR NormalizeSwizzleChar(TCHAR C)
		{
			switch (C)
			{
			case TEXT('r'): return TEXT('x');
			case TEXT('g'): return TEXT('y');
			case TEXT('b'): return TEXT('z');
			case TEXT('a'): return TEXT('w');
			default: return C;
			}
		}

		static int32 SwizzleCharToIndex(TCHAR C)
		{
			switch (NormalizeSwizzleChar(C))
			{
			case TEXT('x'): return 0;
			case TEXT('y'): return 1;
			case TEXT('z'): return 2;
			case TEXT('w'): return 3;
			default: return 0;
			}
		}

		FString ComputeEffectiveSwizzle(const FString& Swizzle, bool bIgnoreDstMask = false) const
		{
			if (Swizzle.IsEmpty()) return TEXT("");

			FString Eff;
			if (Swizzle.Len() == 4)
			{
				if (CurrentOpCode == TEXT("dp2") || CurrentOpCode.StartsWith(TEXT("sample")))
				{
					Eff = Swizzle.Left(2);
				}
				else if (CurrentOpCode == TEXT("dp3"))
				{
					Eff = Swizzle.Left(3);
				}
				else if (!bIgnoreDstMask && !CurrentDstMask.IsEmpty() && CurrentOpCode != TEXT("dp4"))
				{
					if (CurrentDstMask.Contains(TEXT("x"))) Eff.AppendChar(Swizzle[0]);
					if (CurrentDstMask.Contains(TEXT("y"))) Eff.AppendChar(Swizzle[1]);
					if (CurrentDstMask.Contains(TEXT("z"))) Eff.AppendChar(Swizzle[2]);
					if (CurrentDstMask.Contains(TEXT("w"))) Eff.AppendChar(Swizzle[3]);
				}
				if (Eff.IsEmpty()) Eff = Swizzle;
			}
			else
			{
				Eff = Swizzle;
			}

			for (int32 i = 0; i < Eff.Len(); ++i)
				Eff[i] = NormalizeSwizzleChar(Eff[i]);
			return Eff;
		}

		FExprPin ApplyMask(const FExprPin& Src, const FString& Swizzle, bool bIgnoreDstMask = false)
		{
			if (!Src.IsValid() || Swizzle.IsEmpty()) return Src;

			FString Eff = ComputeEffectiveSwizzle(Swizzle, bIgnoreDstMask);
			if (Eff.IsEmpty()) return Src;

			// Fast-path: Texture / VectorParameter / ParticleColor / VertexColor expose dedicated RGB and A pins!
			const bool bIsVertexColor = Src.Expr->IsA<UMaterialExpressionVertexColor>();
			const bool bHasRgbaPins =
				Src.Expr->IsA<UMaterialExpressionTextureBase>() ||
				Src.Expr->IsA<UMaterialExpressionVectorParameter>() ||
				Src.Expr->IsA<UMaterialExpressionParticleColor>() ||
				bIsVertexColor;
			const FString RgbPinName = bIsVertexColor ? TEXT("") : TEXT("RGB");

			bool bAllSameChar = (Eff.Len() >= 1);
			for (int32 i = 1; i < Eff.Len(); ++i)
			{
				if (Eff[i] != Eff[0]) { bAllSameChar = false; break; }
			}

			if (bHasRgbaPins)
			{
				if (bAllSameChar && Eff[0] == TEXT('w'))
					return { Src.Expr, TEXT("A"), 1, Src.Depth, false };
				if (Eff.Equals(TEXT("xyz")))
					return { Src.Expr, RgbPinName, 3, Src.Depth, false };
			}

			if (Src.NumChannels == 1) return Src;
			if (Eff.Equals(TEXT("xyzw")) && Src.NumChannels == 4) return Src;

			// Replicated scalar lane (e.g. "xxx", "wwww") collapses to float1 outside texture UVs / dot products
			if (bAllSameChar && Eff.Len() > 1 && !CurrentOpCode.StartsWith(TEXT("sample")) && !CurrentOpCode.StartsWith(TEXT("dp")))
				Eff = Eff.Left(1);

			auto ExtractSingleChannel = [&](TCHAR Ch) -> FExprPin
			{
				int32 Idx = SwizzleCharToIndex(Ch);
				if (bHasRgbaPins && Idx == 3 && Src.NumChannels >= 4)
					return { Src.Expr, TEXT("A"), 1, Src.Depth, false };
				if (Idx >= Src.NumChannels) Idx = FMath::Max(0, Src.NumChannels - 1);
				if (Src.NumChannels == 1) return Src;
				const int32 D = FMath::Max(0, Src.Depth - 1);
				UMaterialExpressionComponentMask* M = Spawn<UMaterialExpressionComponentMask>(D);
				if (!M) return Src;
				M->R = (Idx == 0) ? 1u : 0u;
				M->G = (Idx == 1) ? 1u : 0u;
				M->B = (Idx == 2) ? 1u : 0u;
				M->A = (Idx == 3) ? 1u : 0u;
				ConnectSmart(Src.Expr, Src.OutputPin, M, TEXT(""));
				return { M, TEXT(""), 1, D, false };
			};

			bool bStrictlyAscending = true;
			for (int32 i = 1; i < Eff.Len(); ++i)
			{
				if (SwizzleCharToIndex(Eff[i]) <= SwizzleCharToIndex(Eff[i - 1]))
				{
					bStrictlyAscending = false;
					break;
				}
			}

			if (bStrictlyAscending)
			{
				uint32 R = Eff.Contains(TEXT("x")) ? 1u : 0u;
				uint32 G = Eff.Contains(TEXT("y")) ? 1u : 0u;
				uint32 B = Eff.Contains(TEXT("z")) ? 1u : 0u;
				uint32 A = Eff.Contains(TEXT("w")) ? 1u : 0u;

				// Clamp mask bits to available channels so SM6 NEVER errors with "Not enough components for component mask"
				if (Src.NumChannels < 4) A = 0;
				if (Src.NumChannels < 3) B = 0;
				if (Src.NumChannels < 2) G = 0;
				if (R + G + B + A == 0) R = 1;

				// No-op mask check (e.g. masking RG on an existing float2)
				const int32 OutCh = static_cast<int32>(R + G + B + A);
				if (OutCh == Src.NumChannels &&
					((OutCh == 2 && R && G) || (OutCh == 3 && R && G && B) || (OutCh == 4 && R && G && B && A)))
				{
					return Src;
				}

				const int32 D = FMath::Max(0, Src.Depth - 1);
				UMaterialExpressionComponentMask* Mask = Spawn<UMaterialExpressionComponentMask>(D);
				if (!Mask) return Src;
				Mask->R = R; Mask->G = G; Mask->B = B; Mask->A = A;
				ConnectSmart(Src.Expr, Src.OutputPin, Mask, TEXT(""));
				return { Mask, TEXT(""), OutCh, D, false };
			}

			// Permuted or repeated swizzle (e.g. "yzx", "zxy", "zy", "xxy") -> extract per-lane and AppendVector
			FExprPin Acc = ExtractSingleChannel(Eff[0]);
			for (int32 i = 1; i < Eff.Len(); ++i)
			{
				FExprPin Lane = ExtractSingleChannel(Eff[i]);
				const int32 D = FMath::Max(0, FMath::Min(Acc.Depth, Lane.Depth) - 1);
				UMaterialExpressionAppendVector* App = Spawn<UMaterialExpressionAppendVector>(D);
				if (!App) break;
				ConnectSmart(Acc.Expr, Acc.OutputPin, App, TEXT("A"));
				ConnectSmart(Lane.Expr, Lane.OutputPin, App, TEXT("B"));
				Acc = { App, TEXT(""), Acc.NumChannels + 1, D, false };
			}
			return Acc;
		}

		// Ensure two vector operands have compatible channel counts for UE5 SM6 binary ops (e.g. float4 vs float3 -> float3)
		void MatchBinaryChannels(FExprPin& A, FExprPin& B)
		{
			if (!A.IsValid() || !B.IsValid()) return;
			if (A.NumChannels == 1 || B.NumChannels == 1 || A.NumChannels == B.NumChannels) return;
			const int32 TargetCh = FMath::Min(A.NumChannels, B.NumChannels);
			const FString MaskStr = (TargetCh == 2) ? TEXT("xy") : (TargetCh == 3) ? TEXT("xyz") : TEXT("x");
			if (A.NumChannels > TargetCh) A = ApplyMask(A, MaskStr, true);
			if (B.NumChannels > TargetCh) B = ApplyMask(B, MaskStr, true);
		}

		static bool MasksOverlap(const FString& A, const FString& B)
		{
			for (int32 i = 0; i < B.Len(); ++i)
			{
				if (A.Contains(FString::Chr(B[i]))) return true;
			}
			return false;
		}

		FExprPin CombinePins(const FExprPin& A, const FExprPin& B)
		{
			if (A.bIsBoilerplate && B.bIsBoilerplate) return A;
			if (!A.IsValid()) return B;
			if (!B.IsValid()) return A;
			const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
			UMaterialExpressionAppendVector* App = Spawn<UMaterialExpressionAppendVector>(D);
			if (!App) return A;
			ConnectSmart(A.Expr, A.OutputPin, App, TEXT("A"));
			ConnectSmart(B.Expr, B.OutputPin, App, TEXT("B"));
			return { App, TEXT(""), A.NumChannels + B.NumChannels, D, false };
		}

		void AddTrackedReg(TSet<FString>& Set, const FString& DstRaw)
		{
			FString Base, Mask;
			SplitRegSwizzle(DstRaw, Base, Mask);
			if (Base.IsEmpty() || Base.Equals(TEXT("null"), ESearchCase::IgnoreCase)) return;
			if (Mask.IsEmpty() || Mask.Equals(TEXT("xyzw")))
			{
				Set.Add(Base);
				Set.Add(Base + TEXT(".x"));
				Set.Add(Base + TEXT(".y"));
				Set.Add(Base + TEXT(".z"));
				Set.Add(Base + TEXT(".w"));
			}
			else
			{
				Set.Add(Base + TEXT(".") + Mask);
				for (int32 k = 0; k < Mask.Len(); ++k)
					Set.Add(FString::Printf(TEXT("%s.%c"), *Base, NormalizeSwizzleChar(Mask[k])));
			}
		}

		bool IsTrackedReg(const TSet<FString>& Set, FString RawOp) const
		{
			RawOp.TrimStartAndEndInline();
			if (RawOp.StartsWith(TEXT("-"))) RawOp.RightChopInline(1);
			if (RawOp.StartsWith(TEXT("|")) && RawOp.EndsWith(TEXT("|"))) RawOp = RawOp.Mid(1, RawOp.Len() - 2);
			if (RawOp.IsEmpty()) return false;
			if (Set.Contains(RawOp)) return true;
			FString Base, Swiz;
			SplitRegSwizzle(RawOp, Base, Swiz);
			if (Set.Contains(Base)) return true;
			FString Eff = ComputeEffectiveSwizzle(Swiz, false);
			if (!Eff.IsEmpty())
			{
				if (Set.Contains(Base + TEXT(".") + Eff)) return true;
				if (Set.Contains(FString::Printf(TEXT("%s.%c"), *Base, Eff[0]))) return true;
			}
			return false;
		}

		static void ClearTrackedRegSet(TSet<FString>& Set, const FString& Base, const FString& Mask)
		{
			Set.Remove(Base);
			const FString Prefix = Base + TEXT(".");
			if (Mask.IsEmpty() || Mask.Equals(TEXT("xyzw")))
			{
				for (auto It = Set.CreateIterator(); It; ++It)
				{
					if (It->StartsWith(Prefix)) It.RemoveCurrent();
				}
			}
			else
			{
				Set.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				for (auto It = Set.CreateIterator(); It; ++It)
				{
					if (It->StartsWith(Prefix) && MasksOverlap(It->Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
			}
		}

		void ClearCondsForWrite(const FString& Base, const FString& Mask)
		{
			if (!Mask.IsEmpty() && !Mask.Equals(TEXT("xyzw")))
			{
				PowCondRegs.Remove(Base);
				PowCondRegs.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				PowLogBases.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				CompareConds.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				for (int32 i = 0; i < Mask.Len(); ++i)
				{
					const FString LaneKey = FString::Printf(TEXT("%s.%c"), *Base, Mask[i]);
					PowCondRegs.Remove(LaneKey);
					PowLogBases.Remove(LaneKey);
					CompareConds.Remove(LaneKey);
				}
				const FString Prefix = Base + TEXT(".");
				for (auto It = CompareConds.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix) && MasksOverlap(It.Key().Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
			}
			else
			{
				PowCondRegs.Remove(Base);
				PowLogBases.Remove(Base);
				CompareConds.Remove(Base);
				const FString Prefix = Base + TEXT(".");
				for (auto It = CompareConds.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix)) It.RemoveCurrent();
				}
			}
			if (Base.Equals(TranslatedWorldPosReg) && (Mask.IsEmpty() || Mask.Contains(TEXT("x")) || Mask.Contains(TEXT("y")) || Mask.Contains(TEXT("z"))))
				TranslatedWorldPosReg.Empty();
			if (Base.Equals(CameraVecRsqBase) && (Mask.IsEmpty() || Mask.Contains(FString::Chr(CameraVecRsqLane))))
				CameraVecRsqReg.Empty();
			ClearTrackedRegSet(SvPosRectRegs, Base, Mask);
			ClearTrackedRegSet(PixelDepthRegs, Base, Mask);
			ClearTrackedRegSet(SceneDepthRegs, Base, Mask);
			ClearTrackedRegSet(SceneDepthDiffRegs, Base, Mask);
			ClearTrackedRegSet(SceneDepthDiffRegs, Base, Mask);
			PrimStructRowOffsets.Remove(Base);
			if (Mask.IsEmpty() || Mask.Equals(TEXT("xyzw")))
			{
				const FString Prefix = Base + TEXT(".");
				for (auto It = PowCondRegs.CreateIterator(); It; ++It)
				{
					if (It->StartsWith(Prefix)) It.RemoveCurrent();
				}
				for (auto It = PowLogBases.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix)) It.RemoveCurrent();
				}
				for (auto It = CompareConds.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix)) It.RemoveCurrent();
				}
				for (auto It = PrimStructRowOffsets.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix)) It.RemoveCurrent();
				}
			}
			else
			{
				PowCondRegs.Remove(Base);
				PowCondRegs.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				PowLogBases.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				CompareConds.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
				const FString Prefix = Base + TEXT(".");
				for (auto It = PowCondRegs.CreateIterator(); It; ++It)
				{
					if (It->StartsWith(Prefix) && MasksOverlap(It->Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
				for (auto It = PowLogBases.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix) && MasksOverlap(It.Key().Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
				for (auto It = CompareConds.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix) && MasksOverlap(It.Key().Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
				for (auto It = PrimStructRowOffsets.CreateIterator(); It; ++It)
				{
					if (It.Key().StartsWith(Prefix) && MasksOverlap(It.Key().Mid(Prefix.Len()), Mask))
						It.RemoveCurrent();
				}
			}
		}

		void SetReg(const FString& DstRaw, const FExprPin& Pin)
		{
			FString Base, Mask;
			SplitRegSwizzle(DstRaw, Base, Mask);
			if (Base.IsEmpty() || Base.Equals(TEXT("null"), ESearchCase::IgnoreCase)) return;

			ClearCondsForWrite(Base, Mask);

			if (Mask.IsEmpty() || Mask.Equals(TEXT("xyzw")))
			{
				static const TCHAR* AllSubs[] = { TEXT("x"), TEXT("y"), TEXT("z"), TEXT("w"),
					TEXT("xyz"), TEXT("xzw"), TEXT("xyw"), TEXT("yzw"),
					TEXT("xy"), TEXT("yz"), TEXT("zw"), TEXT("xz"), TEXT("xw"), TEXT("yw") };
				for (const TCHAR* Sub : AllSubs)
					RegValues.Remove(FString::Printf(TEXT("%s.%s"), *Base, Sub));
				RegValues.Add(Base, Pin);
				return;
			}

			// Split any existing overlapping aggregate sub-register into unaffected scalar lanes before overwriting
			struct FSubRegDef { const TCHAR* KeySuffix; const TCHAR* Lanes; };
			static const FSubRegDef Aggregates[] = {
				{ TEXT(""), TEXT("xyzw") },
				{ TEXT(".xyz"), TEXT("xyz") },
				{ TEXT(".xzw"), TEXT("xzw") },
				{ TEXT(".xyw"), TEXT("xyw") },
				{ TEXT(".yzw"), TEXT("yzw") },
				{ TEXT(".xy"), TEXT("xy") },
				{ TEXT(".yz"), TEXT("yz") },
				{ TEXT(".zw"), TEXT("zw") },
				{ TEXT(".xz"), TEXT("xz") },
				{ TEXT(".xw"), TEXT("xw") },
				{ TEXT(".yw"), TEXT("yw") }
			};

			for (const FSubRegDef& Def : Aggregates)
			{
				if (!MasksOverlap(Def.Lanes, Mask)) continue;
				const FString AggKey = Base + Def.KeySuffix;
				if (FExprPin* ExistingPtr = RegValues.Find(AggKey))
				{
					FExprPin OldPin = *ExistingPtr;
					RegValues.Remove(AggKey);
					if ((OldPin.IsValid() || OldPin.bIsBoilerplate) && !FString(Def.Lanes).Equals(Mask))
					{
						const bool bHasRgba = OldPin.IsValid() && (
							OldPin.Expr->IsA<UMaterialExpressionTextureBase>() ||
							OldPin.Expr->IsA<UMaterialExpressionVectorParameter>() ||
							OldPin.Expr->IsA<UMaterialExpressionParticleColor>() ||
							OldPin.Expr->IsA<UMaterialExpressionVertexColor>());
						const FString LanesStr(Def.Lanes);
						for (int32 k = 0; k < LanesStr.Len(); ++k)
						{
							const TCHAR LaneCh = LanesStr[k];
							if (!Mask.Contains(FString::Chr(LaneCh)) &&
								(OldPin.bIsBoilerplate || OldPin.NumChannels == 1 || k < OldPin.NumChannels || (bHasRgba && k == 3)))
							{
								FExprPin Preserved;
								if (OldPin.bIsBoilerplate || OldPin.NumChannels == 1)
								{
									Preserved = OldPin;
								}
								else if (bHasRgba && k == 3)
								{
									Preserved = { OldPin.Expr, TEXT("A"), 1, OldPin.Depth, false };
								}
								else
								{
									const TCHAR SrcCh[2] = { TEXT("xyzw")[k], 0 };
									Preserved = ApplyMask(OldPin, SrcCh, true);
								}
								RegValues.Add(FString::Printf(TEXT("%s.%c"), *Base, LaneCh), Preserved);
							}
						}
					}
				}
			}

			for (int32 i = 0; i < Mask.Len(); ++i)
				RegValues.Remove(FString::Printf(TEXT("%s.%c"), *Base, Mask[i]));

			if (Mask.Len() > 1 && Pin.NumChannels == 1 && Pin.IsValid() && !Pin.bIsBoilerplate)
			{
				FExprPin Acc = Pin;
				for (int32 i = 1; i < Mask.Len(); ++i)
					Acc = CombinePins(Acc, Pin);
				RegValues.Add(FString::Printf(TEXT("%s.%s"), *Base, *Mask), Acc);
			}
			else if (!Pin.IsValid() && !Pin.bIsBoilerplate)
			{
				RegValues.Remove(FString::Printf(TEXT("%s.%s"), *Base, *Mask));
			}
			else
			{
				RegValues.Add(FString::Printf(TEXT("%s.%s"), *Base, *Mask), Pin);
			}
		}

		bool ExtractLaneFromStored(const FString& Base, TCHAR Ch, FExprPin& OutPin, bool bAllowWholeBase = true)
		{
			const FString LaneKey = FString::Printf(TEXT("%s.%c"), *Base, Ch);
			if (FExprPin* Direct = RegValues.Find(LaneKey))
			{
				OutPin = *Direct;
				return OutPin.IsValid() || OutPin.bIsBoilerplate;
			}

			struct FSubRegDef { const TCHAR* KeySuffix; const TCHAR* Lanes; };
			static const FSubRegDef Sources[] = {
				{ TEXT(".xyz"), TEXT("xyz") },
				{ TEXT(".xzw"), TEXT("xzw") },
				{ TEXT(".xyw"), TEXT("xyw") },
				{ TEXT(".yzw"), TEXT("yzw") },
				{ TEXT(".xy"), TEXT("xy") },
				{ TEXT(".yz"), TEXT("yz") },
				{ TEXT(".zw"), TEXT("zw") },
				{ TEXT(".xz"), TEXT("xz") },
				{ TEXT(".xw"), TEXT("xw") },
				{ TEXT(".yw"), TEXT("yw") },
				{ TEXT(""), TEXT("xyzw") }
			};

			for (const FSubRegDef& Def : Sources)
			{
				if (!bAllowWholeBase && Def.KeySuffix[0] == 0) continue;
				const FString LanesStr(Def.Lanes);
				int32 LocalIdx = INDEX_NONE;
				if (!LanesStr.FindChar(Ch, LocalIdx)) continue;
				if (FExprPin* SrcPtr = RegValues.Find(Base + Def.KeySuffix))
				{
					FExprPin SrcPin = *SrcPtr;
					if (SrcPin.bIsBoilerplate)
					{
						OutPin = SrcPin;
						return true;
					}
					if (!SrcPin.IsValid()) continue;
					if (SrcPin.NumChannels > 1 && LocalIdx >= SrcPin.NumChannels) continue;
					const TCHAR SrcCh[2] = { TEXT("xyzw")[LocalIdx], 0 };
					OutPin = (SrcPin.NumChannels == 1) ? SrcPin : ApplyMask(SrcPin, SrcCh, true);
					return OutPin.IsValid() || OutPin.bIsBoilerplate;
				}
			}
			return false;
		}

		bool ResolveReg(const FString& Base, const FString& RawSwizzle, FExprPin& OutPin)
		{
			struct FSubRegDef { const TCHAR* KeySuffix; const TCHAR* Lanes; };
			static const FSubRegDef SubDefs[] = {
				{ TEXT(".xyz"), TEXT("xyz") },
				{ TEXT(".xzw"), TEXT("xzw") },
				{ TEXT(".xyw"), TEXT("xyw") },
				{ TEXT(".yzw"), TEXT("yzw") },
				{ TEXT(".xy"), TEXT("xy") },
				{ TEXT(".yz"), TEXT("yz") },
				{ TEXT(".zw"), TEXT("zw") },
				{ TEXT(".xz"), TEXT("xz") },
				{ TEXT(".xw"), TEXT("xw") },
				{ TEXT(".yw"), TEXT("yw") }
			};
			static const TCHAR* FallbackSubs[] = {
				TEXT(".xyz"), TEXT(".xzw"), TEXT(".xyw"), TEXT(".yzw"),
				TEXT(".xy"), TEXT(".yz"), TEXT(".zw"), TEXT(".xz"), TEXT(".xw"), TEXT(".yw"),
				TEXT(".x"), TEXT(".y"), TEXT(".z"), TEXT(".w")
			};

			FString Eff = ComputeEffectiveSwizzle(RawSwizzle, false);
			if (Eff.IsEmpty())
			{
				if (FExprPin* Whole = RegValues.Find(Base))
				{
					OutPin = *Whole;
					return OutPin.IsValid() || OutPin.bIsBoilerplate;
				}
				FExprPin* Xy = RegValues.Find(Base + TEXT(".xy"));
				FExprPin* Zw = RegValues.Find(Base + TEXT(".zw"));
				if (Xy && Zw)
				{
					FExprPin XyC = *Xy, ZwC = *Zw;
					OutPin = CombinePins(XyC, ZwC);
					RegValues.Add(Base, OutPin);
					return true;
				}
				FExprPin* Xyz = RegValues.Find(Base + TEXT(".xyz"));
				FExprPin* WOnly = RegValues.Find(Base + TEXT(".w"));
				if (Xyz && WOnly)
				{
					FExprPin XyzC = *Xyz, WC = *WOnly;
					OutPin = CombinePins(XyzC, WC);
					RegValues.Add(Base, OutPin);
					return true;
				}
				for (const TCHAR* Sub : FallbackSubs)
				{
					if (FExprPin* Found = RegValues.Find(Base + Sub))
					{
						OutPin = *Found;
						return OutPin.IsValid() || OutPin.bIsBoilerplate;
					}
				}
				return false;
			}

			bool bAllSame = (Eff.Len() >= 1);
			for (int32 i = 1; i < Eff.Len(); ++i)
			{
				if (Eff[i] != Eff[0]) { bAllSame = false; break; }
			}
			if (bAllSame && Eff.Len() > 1 && !CurrentOpCode.StartsWith(TEXT("sample")) && CurrentOpCode != TEXT("dp4"))
				Eff = Eff.Left(1);

			// 1. Exact register / exact sub-register match
			if (Eff.Equals(TEXT("xyzw")))
			{
				if (FExprPin* Whole = RegValues.Find(Base))
				{
					OutPin = *Whole;
					return OutPin.IsValid() || OutPin.bIsBoilerplate;
				}
			}
			else if (FExprPin* Exact = RegValues.Find(Base + TEXT(".") + Eff))
			{
				OutPin = *Exact;
				return OutPin.IsValid() || OutPin.bIsBoilerplate;
			}

			// 2. Whole register stored -> slice Eff directly
			if (FExprPin* Whole = RegValues.Find(Base))
			{
				FExprPin WholeCopy = *Whole;
				if (WholeCopy.bIsBoilerplate)
				{
					OutPin = WholeCopy;
					return true;
				}
				OutPin = (Eff.Equals(TEXT("xyzw")) && WholeCopy.NumChannels == 4) ? WholeCopy : ApplyMask(WholeCopy, Eff, true);
				return OutPin.IsValid();
			}

			// 3. Single-lane query
			if (Eff.Len() == 1)
			{
				FExprPin Extracted;
				if (ExtractLaneFromStored(Base, Eff[0], Extracted, false))
				{
					RegValues.Add(Base + TEXT(".") + Eff, Extracted);
					OutPin = Extracted;
					return true;
				}
				return false;
			}

			// 4. Single stored sub-register remap (e.g. "yzx" inside ".xyz", "yzw" inside ".yzw", "zxw" inside ".xzw")
			for (const FSubRegDef& Def : SubDefs)
			{
				if (FExprPin* SubPtr = RegValues.Find(Base + Def.KeySuffix))
				{
					const FString LanesStr(Def.Lanes);
					FString LocalSwiz;
					bool bAllCovered = true;
					for (int32 k = 0; k < Eff.Len(); ++k)
					{
						int32 LocalIdx = INDEX_NONE;
						if (!LanesStr.FindChar(Eff[k], LocalIdx))
						{
							bAllCovered = false;
							break;
						}
						LocalSwiz.AppendChar(TEXT("xyzw")[LocalIdx]);
					}
					if (bAllCovered)
					{
						FExprPin SubCopy = *SubPtr;
						if (SubCopy.bIsBoilerplate)
						{
							OutPin = SubCopy;
							return true;
						}
						if (SubCopy.IsValid())
						{
							OutPin = ApplyMask(SubCopy, LocalSwiz, true);
							return OutPin.IsValid();
						}
					}
				}
			}

			// 5. Multi-fragment assembly
			if (Eff.Equals(TEXT("xyzw")))
			{
				FExprPin* Xy = RegValues.Find(Base + TEXT(".xy"));
				FExprPin* Zw = RegValues.Find(Base + TEXT(".zw"));
				if (Xy && Zw)
				{
					FExprPin XyC = *Xy, ZwC = *Zw;
					OutPin = CombinePins(XyC, ZwC);
					RegValues.Add(Base, OutPin);
					return OutPin.IsValid() || OutPin.bIsBoilerplate;
				}
				FExprPin* Xyz = RegValues.Find(Base + TEXT(".xyz"));
				FExprPin* WOnly = RegValues.Find(Base + TEXT(".w"));
				if (Xyz && WOnly)
				{
					FExprPin XyzC = *Xyz, WC = *WOnly;
					OutPin = CombinePins(XyzC, WC);
					RegValues.Add(Base, OutPin);
					return OutPin.IsValid() || OutPin.bIsBoilerplate;
				}
			}
			else if (Eff.Len() == 3)
			{
				FExprPin Extra;
				if (FExprPin* Prefix2 = RegValues.Find(Base + TEXT(".") + Eff.Left(2)))
				{
					FExprPin P2C = *Prefix2;
					if (ExtractLaneFromStored(Base, Eff[2], Extra, false))
					{
						OutPin = CombinePins(P2C, Extra);
						bool bAsc = SwizzleCharToIndex(Eff[0]) < SwizzleCharToIndex(Eff[1]) && SwizzleCharToIndex(Eff[1]) < SwizzleCharToIndex(Eff[2]);
						if (bAsc) RegValues.Add(Base + TEXT(".") + Eff, OutPin);
						return OutPin.IsValid() || OutPin.bIsBoilerplate;
					}
				}
				if (FExprPin* Suffix2 = RegValues.Find(Base + TEXT(".") + Eff.Right(2)))
				{
					FExprPin S2C = *Suffix2;
					if (ExtractLaneFromStored(Base, Eff[0], Extra, false))
					{
						OutPin = CombinePins(Extra, S2C);
						bool bAsc = SwizzleCharToIndex(Eff[0]) < SwizzleCharToIndex(Eff[1]) && SwizzleCharToIndex(Eff[1]) < SwizzleCharToIndex(Eff[2]);
						if (bAsc) RegValues.Add(Base + TEXT(".") + Eff, OutPin);
						return OutPin.IsValid() || OutPin.bIsBoilerplate;
					}
				}
			}

			TArray<FExprPin> AssembledLanes;
			AssembledLanes.Reserve(Eff.Len());
			bool bAllFound = true;
			bool bAnyValid = false;
			for (int32 k = 0; k < Eff.Len(); ++k)
			{
				FExprPin LanePin;
				if (!ExtractLaneFromStored(Base, Eff[k], LanePin, false))
				{
					bAllFound = false;
					break;
				}
				if (LanePin.IsValid()) bAnyValid = true;
				AssembledLanes.Add(LanePin);
			}
			if (bAllFound && AssembledLanes.Num() == Eff.Len())
			{
				if (!bAnyValid)
				{
					OutPin = AssembledLanes[0];
					return true;
				}
				FExprPin Acc = AssembledLanes[0];
				for (int32 k = 1; k < AssembledLanes.Num(); ++k)
					Acc = CombinePins(Acc, AssembledLanes[k]);

				bool bStrictlyAsc = true;
				for (int32 k = 1; k < Eff.Len(); ++k)
				{
					if (SwizzleCharToIndex(Eff[k]) <= SwizzleCharToIndex(Eff[k - 1])) { bStrictlyAsc = false; break; }
				}
				if (bStrictlyAsc)
					RegValues.Add(Eff.Equals(TEXT("xyzw")) ? Base : (Base + TEXT(".") + Eff), Acc);

				OutPin = Acc;
				return OutPin.IsValid() || OutPin.bIsBoilerplate;
			}

			// 6. Partial fallback (only if stored lanes actually overlap the requested swizzle!)
			for (const TCHAR* Sub : FallbackSubs)
			{
				if (!MasksOverlap(Sub + 1, Eff)) continue;
				if (FExprPin* Found = RegValues.Find(Base + Sub))
				{
					FExprPin FoundCopy = *Found;
					if (FoundCopy.bIsBoilerplate)
					{
						OutPin = FoundCopy;
						return true;
					}
					OutPin = (FoundCopy.NumChannels > 1) ? ApplyMask(FoundCopy, Eff, true) : FoundCopy;
					return OutPin.IsValid();
				}
			}
			return false;
		}

		UMaterialExpressionParticleColor* GetOrSpawnParticleColor()
		{
			if (!CachedParticleColor)
				CachedParticleColor = Spawn<UMaterialExpressionParticleColor>(3);
			return CachedParticleColor;
		}

		UMaterialExpressionDynamicParameter* GetOrSpawnDynamicParam(int32 ParamIndex = 0)
		{
			if (UMaterialExpressionDynamicParameter** Found = CachedDynamicParams.Find(ParamIndex))
				return *Found;
			UMaterialExpressionDynamicParameter* DP = Spawn<UMaterialExpressionDynamicParameter>(4);
			if (DP)
			{
				DP->ParameterIndex = static_cast<uint32>(FMath::Clamp(ParamIndex, 0, 3));
				CachedDynamicParam = DP;
			}
			CachedDynamicParams.Add(ParamIndex, DP);
			return DP;
		}

		static FString DynamicParamPinForLane(UMaterialExpressionDynamicParameter* DP, int32 DynIdx)
		{
			if (DP)
			{
				const TArray<FExpressionOutput>& Outs = DP->GetOutputs();
				if (Outs.IsValidIndex(DynIdx) && !Outs[DynIdx].OutputName.IsNone())
					return Outs[DynIdx].OutputName.ToString();
			}
			return (DynIdx == 1) ? TEXT("G") : (DynIdx == 2) ? TEXT("B") : (DynIdx == 3) ? TEXT("A") : TEXT("R");
		}

		FExprPin GetOrSpawnTexCoord(int32 CoordIdx)
		{
			if (FExprPin* Cached = CachedTexCoords.Find(CoordIdx))
				return *Cached;
			UMaterialExpressionTextureCoordinate* UV = Spawn<UMaterialExpressionTextureCoordinate>(7);
			if (UV) UV->CoordinateIndex = CoordIdx;
			FExprPin Pin = { UV, TEXT(""), 2, 7, false };
			CachedTexCoords.Add(CoordIdx, Pin);
			return Pin;
		}

		FExprPin ResolveOperand(FString RawOp)
		{
			RawOp.TrimStartAndEndInline();
			bool bNegate = false;
			bool bAbs = false;
			if (RawOp.StartsWith(TEXT("-")))
			{
				bNegate = true;
				RawOp.RightChopInline(1);
			}
			if (RawOp.StartsWith(TEXT("|")) && RawOp.EndsWith(TEXT("|")))
			{
				bAbs = true;
				RawOp = RawOp.Mid(1, RawOp.Len() - 2);
			}

			FExprPin Result;
			if (RawOp.StartsWith(TEXT("l(")) && RawOp.EndsWith(TEXT(")")))
			{
				FString Inner = RawOp.Mid(2, RawOp.Len() - 3);
				TArray<FString> Parts;
				Inner.ParseIntoArray(Parts, TEXT(","), true);
				TArray<float> Vals;
				for (FString& P : Parts) { P.TrimStartAndEndInline(); Vals.Add(FCString::Atof(*P)); }
				// In DXBC, 4-lane literals l(a,b,c,d) are aligned to the destination write mask (e.g. .zw -> (c,d), .xzw -> (a,c,d)),
				// EXCEPT for dot products (dp2/dp3/dp4) which always consume lanes xy/xyz/xyzw regardless of dst mask!
				if (Vals.Num() == 4 && !CurrentDstMask.IsEmpty() && !CurrentDstMask.Equals(TEXT("xyzw")) && !CurrentOpCode.StartsWith(TEXT("dp")))
				{
					TArray<float> Aligned;
					if (CurrentDstMask.Contains(TEXT("x"))) Aligned.Add(Vals[0]);
					if (CurrentDstMask.Contains(TEXT("y"))) Aligned.Add(Vals[1]);
					if (CurrentDstMask.Contains(TEXT("z"))) Aligned.Add(Vals[2]);
					if (CurrentDstMask.Contains(TEXT("w"))) Aligned.Add(Vals[3]);
					if (Aligned.Num() > 0)
						Vals = MoveTemp(Aligned);
				}
				else if (Vals.Num() == 4 && CurrentOpCode == TEXT("dp2"))
				{
					Vals.SetNum(2);
				}
				else if (Vals.Num() == 4 && CurrentOpCode == TEXT("dp3"))
				{
					Vals.SetNum(3);
				}
				if (Vals.Num() <= 1)
				{
					Result = MakeConst(Vals.Num() == 1 ? Vals[0] : 0.0f, 6);
				}
				else if (Vals.Num() == 2 || (Vals.Num() == 4 && FMath::IsNearlyZero(Vals[2]) && FMath::IsNearlyZero(Vals[3])))
				{
					UMaterialExpressionConstant2Vector* C2 = Spawn<UMaterialExpressionConstant2Vector>(6);
					if (C2) { C2->R = Vals[0]; C2->G = Vals[1]; }
					Result = { C2, TEXT(""), 2, 6, false };
				}
				else if (Vals.Num() == 3 || (Vals.Num() == 4 && FMath::IsNearlyZero(Vals[3])))
				{
					UMaterialExpressionConstant3Vector* C3 = Spawn<UMaterialExpressionConstant3Vector>(6);
					if (C3) C3->Constant = FLinearColor(Vals[0], Vals[1], Vals[2], 1.0f);
					Result = { C3, TEXT(""), 3, 6, false };
				}
				else
				{
					UMaterialExpressionConstant4Vector* C4 = Spawn<UMaterialExpressionConstant4Vector>(6);
					if (C4) C4->Constant = FLinearColor(Vals[0], Vals[1], Vals[2], Vals[3]);
					Result = { C4, TEXT(""), 4, 6, false };
				}
			}
			else
			{
				FString Base, Swizzle;
				SplitRegSwizzle(RawOp, Base, Swizzle);

				// Check parsed DXBC input semantics (v0..v11)
				if (const EInputSemantic* Sem = InputSemantics.Find(Base))
				{
					if (*Sem == EInputSemantic::Boilerplate || *Sem == EInputSemantic::SvPosition)
					{
						Result.bIsBoilerplate = true;
						return Result;
					}
					if (*Sem == EInputSemantic::ParticleColor)
					{
						UMaterialExpressionParticleColor* PC = GetOrSpawnParticleColor();
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xyzw") : Swizzle, false);
						bool bAllW = !Eff.IsEmpty();
						for (int32 k = 0; k < Eff.Len(); ++k)
						{
							if (Eff[k] != TEXT('w')) { bAllW = false; break; }
						}
						if (bAllW)
							Result = { PC, TEXT("A"), 1, 3, false };
						else
							Result = ApplyMask({ PC, TEXT("RGB"), 3, 3, false }, Eff, true);
					}
					else if (*Sem == EInputSemantic::VertexColor)
					{
						if (!CachedVertexColor) CachedVertexColor = Spawn<UMaterialExpressionVertexColor>(7);
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xyzw") : Swizzle, false);
						bool bAllW = !Eff.IsEmpty();
						for (int32 k = 0; k < Eff.Len(); ++k)
						{
							if (Eff[k] != TEXT('w')) { bAllW = false; break; }
						}
						if (bAllW)
							Result = { CachedVertexColor, TEXT("A"), 1, 7, false };
						else if (Eff.Equals(TEXT("xyzw")))
							Result = CombinePins({ CachedVertexColor, TEXT(""), 3, 7, false }, { CachedVertexColor, TEXT("A"), 1, 7, false });
						else
							Result = ApplyMask({ CachedVertexColor, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (*Sem == EInputSemantic::VertexNormalWS)
					{
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xyz") : Swizzle, false);
						bool bAllW = !Eff.IsEmpty();
						for (int32 k = 0; k < Eff.Len(); ++k)
						{
							if (Eff[k] != TEXT('w')) { bAllW = false; break; }
						}
						if (bAllW)
						{
							Result = MakeConst(1.0f, 7);
						}
						else
						{
							if (!CachedVertexNormalWS) CachedVertexNormalWS = Spawn<UMaterialExpressionVertexNormalWS>(7);
							Result = ApplyMask({ CachedVertexNormalWS, TEXT(""), 3, 7, false }, Eff, true);
						}
					}
					else if (*Sem == EInputSemantic::VertexTangentWS)
					{
						if (!CachedVertexTangentWS) CachedVertexTangentWS = Spawn<UMaterialExpressionVertexTangentWS>(7);
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xyz") : Swizzle, false);
						Result = ApplyMask({ CachedVertexTangentWS, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (*Sem == EInputSemantic::IsFrontFace)
					{
						if (!CachedTwoSidedSign) CachedTwoSidedSign = Spawn<UMaterialExpressionTwoSidedSign>(7);
						Result = { CachedTwoSidedSign, TEXT(""), 1, 7, false };
					}
					else if (*Sem == EInputSemantic::DynamicParameter)
					{
						const int32* IdxPtr = InputDynParamIndex.Find(Base);
						const int32 ParamIdx = IdxPtr ? *IdxPtr : 0;
						UMaterialExpressionDynamicParameter* DP = GetOrSpawnDynamicParam(ParamIdx);
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("x") : Swizzle, false);
						if (Eff.IsEmpty()) Eff = TEXT("x");
						if (Eff.Equals(TEXT("xyz")))
						{
							Result = { DP, TEXT("RGB"), 3, 4, false };
						}
						else if (Eff.Equals(TEXT("xyzw")))
						{
							Result = { DP, TEXT("RGBA"), 4, 4, false };
						}
						else
						{
							FExprPin Acc = { DP, DynamicParamPinForLane(DP, SwizzleCharToIndex(Eff[0])), 1, 4, false };
							for (int32 k = 1; k < Eff.Len(); ++k)
							{
								FExprPin NextLane = { DP, DynamicParamPinForLane(DP, SwizzleCharToIndex(Eff[k])), 1, 4, false };
								Acc = CombinePins(Acc, NextLane);
							}
							Result = Acc;
						}
					}
					else if (*Sem == EInputSemantic::TexCoord0 || *Sem == EInputSemantic::TexCoord2 || *Sem == EInputSemantic::TexCoord4)
					{
						const int32 BaseCoordIdx = (*Sem == EInputSemantic::TexCoord0) ? 0 : (*Sem == EInputSemantic::TexCoord2) ? 2 : 4;
						FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xy") : Swizzle, false);
						if (Eff.IsEmpty()) Eff = TEXT("xy");
						const bool bHasLow = Eff.Contains(TEXT("x")) || Eff.Contains(TEXT("y"));
						const bool bHasHigh = Eff.Contains(TEXT("z")) || Eff.Contains(TEXT("w"));
						if (bHasLow && !bHasHigh)
						{
							Result = ApplyMask(GetOrSpawnTexCoord(BaseCoordIdx), Eff, true);
						}
						else if (bHasHigh && !bHasLow)
						{
							FString Remapped;
							for (int32 k = 0; k < Eff.Len(); ++k)
								Remapped.AppendChar(Eff[k] == TEXT('z') ? TEXT('x') : TEXT('y'));
							Result = ApplyMask(GetOrSpawnTexCoord(BaseCoordIdx + 1), Remapped, true);
						}
						else
						{
							auto ExtractUvLane = [&](TCHAR Ch) -> FExprPin
							{
								const bool bIsHigh = (Ch == TEXT('z') || Ch == TEXT('w'));
								const TCHAR LocalCh[2] = { (Ch == TEXT('y') || Ch == TEXT('w')) ? TEXT('y') : TEXT('x'), 0 };
								return ApplyMask(GetOrSpawnTexCoord(bIsHigh ? (BaseCoordIdx + 1) : BaseCoordIdx), LocalCh, true);
							};
							FExprPin Acc = ExtractUvLane(Eff[0]);
							for (int32 k = 1; k < Eff.Len(); ++k)
								Acc = CombinePins(Acc, ExtractUvLane(Eff[k]));
							Result = Acc;
						}
					}
				}
				else if (Base.StartsWith(TEXT("cb")))
				{
					FString Eff = ComputeEffectiveSwizzle(Swizzle.IsEmpty() ? TEXT("xyzw") : Swizzle, false);
					if (Eff.IsEmpty()) Eff = TEXT("xyzw");
					bool bAllSameEff = (Eff.Len() >= 1);
					for (int32 k = 1; k < Eff.Len(); ++k)
					{
						if (Eff[k] != Eff[0]) { bAllSameEff = false; break; }
					}
					if (bAllSameEff && Eff.Len() > 1 && !CurrentOpCode.StartsWith(TEXT("dp")) && !CurrentOpCode.StartsWith(TEXT("sample")))
						Eff = Eff.Left(1);

					// View constant buffer special slots (GameTime, VS camera basis, CameraPositionWS, NormalOverrideParameter)
					if (Base.Equals(ViewCbPrefix + TEXT("[142]")) && (Eff.Contains(TEXT("z")) || Eff.Contains(TEXT("w"))))
					{
						if (!CachedTime) CachedTime = Spawn<UMaterialExpressionTime>(7);
						Result = { CachedTime, TEXT(""), 1, 7, false };
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[12]")) || Base.Equals(ViewCbPrefix + TEXT("[16]")) || Base.Equals(ViewCbPrefix + TEXT("[24]")))
					{
						UMaterialExpressionConstant3Vector* C3 = Spawn<UMaterialExpressionConstant3Vector>(7);
						if (C3) C3->Constant = FLinearColor(1.0f, 0.0f, 0.0f, 1.0f);
						Result = ApplyMask({ C3, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[13]")) || Base.Equals(ViewCbPrefix + TEXT("[17]")) || Base.Equals(ViewCbPrefix + TEXT("[25]")))
					{
						UMaterialExpressionConstant3Vector* C3 = Spawn<UMaterialExpressionConstant3Vector>(7);
						if (C3) C3->Constant = FLinearColor(0.0f, 1.0f, 0.0f, 1.0f);
						Result = ApplyMask({ C3, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[14]")) || Base.Equals(ViewCbPrefix + TEXT("[18]")))
					{
						UMaterialExpressionConstant3Vector* C3 = Spawn<UMaterialExpressionConstant3Vector>(7);
						if (C3) C3->Constant = FLinearColor(0.0f, 0.0f, 1.0f, 1.0f);
						Result = ApplyMask({ C3, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[185]")))
					{
						UMaterialExpressionConstant3Vector* C3 = Spawn<UMaterialExpressionConstant3Vector>(7);
						if (C3) C3->Constant = FLinearColor(1.0f, 1.0f, 1.0f, 1.0f);
						Result = ApplyMask({ C3, TEXT(""), 3, 7, false }, Eff, true);
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[67]")))
					{
						FExprPin CamPosPin;
						const FString CamKey = ViewCbPrefix + TEXT("[67]");
						if (FExprPin* Cached = ParamCache.Find(CamKey))
						{
							CamPosPin = *Cached;
						}
						else
						{
							UMaterialExpressionCameraPositionWS* CamPos = Spawn<UMaterialExpressionCameraPositionWS>(8);
							CamPosPin = MakeDemotedWS(CamPos, 7);
							ParamCache.Add(CamKey, CamPosPin);
						}
						Result = ApplyMask(CamPosPin, Eff, true);
					}
					else if (Base.Equals(ViewCbPrefix + TEXT("[138]")))
					{
						if (Eff.Len() == 1)
						{
							Result = MakeConst(Eff[0] == TEXT('w') ? 1.0f : 0.0f, 7);
						}
						else
						{
							UMaterialExpressionConstant4Vector* C4 = Spawn<UMaterialExpressionConstant4Vector>(7);
							if (C4) C4->Constant = FLinearColor(0.0f, 0.0f, 0.0f, 1.0f);
							Result = ApplyMask({ C4, TEXT(""), 4, 7, false }, Eff, true);
						}
					}
					else if (!Base.StartsWith(MaterialCbPrefix + TEXT("[")))
					{
						int32 CbBr = INDEX_NONE;
						const FString CbName = Base.FindChar(TEXT('['), CbBr) ? Base.Left(CbBr).ToLower() : Base.ToLower();
						const TMap<FString, int32>& MpcSlotsMap = bInVertexShader ? VsMpcCbSlots : PsMpcCbSlots;
						int32 MpcCollectionIdx = 0;
						bool bIsMpc = false;
						if (const int32* MappedMpcIdx = MpcSlotsMap.Find(CbName))
						{
							bIsMpc = true;
							MpcCollectionIdx = *MappedMpcIdx;
						}
						else if (!MpcCbPrefix.IsEmpty() && Base.StartsWith(MpcCbPrefix + TEXT("[")))
						{
							bIsMpc = true;
							MpcCollectionIdx = 0;
						}
						if (bIsMpc)
						{
							const FString CacheKey = Base + TEXT("|") + Eff;
							if (FExprPin* Cached = ParamCache.Find(CacheKey))
							{
								Result = *Cached;
							}
							else
							{
								int32 LBr = INDEX_NONE, RBr = INDEX_NONE, SlotIdx = 0;
								if (Base.FindChar(TEXT('['), LBr) && Base.FindChar(TEXT(']'), RBr) && RBr > LBr)
									SlotIdx = FCString::Atoi(*Base.Mid(LBr + 1, RBr - LBr - 1));

								UMaterialParameterCollection* TargetMpc = Mpcs.IsValidIndex(MpcCollectionIdx)
									? Mpcs[MpcCollectionIdx]
									: (MpcCollectionIdx == 0 ? PrimaryMpc : nullptr);

								if (TargetMpc)
								{
									const int32 ScalarCount = TargetMpc->ScalarParameters.Num();
									const int32 ScalarVectors = FMath::DivideAndRoundUp(ScalarCount, 4);
									if (SlotIdx < ScalarVectors)
									{
										auto MakeMpcScalar = [&](int32 ScalarIdx) -> FExprPin
										{
											if (!TargetMpc->ScalarParameters.IsValidIndex(ScalarIdx))
												return MakeConst(0.0f, 6);
											const FCollectionScalarParameter& SP = TargetMpc->ScalarParameters[ScalarIdx];
											const FString LaneKey = FString::Printf(TEXT("MPC|%d|S|%d"), MpcCollectionIdx, ScalarIdx);
											if (FExprPin* CachedLane = ParamCache.Find(LaneKey))
												return *CachedLane;
											UMaterialExpressionCollectionParameter* CP = Spawn<UMaterialExpressionCollectionParameter>(6);
											if (CP)
											{
												CP->Collection = TargetMpc;
												CP->ParameterName = SP.ParameterName;
												CP->ParameterId = SP.Id;
											}
											FExprPin Pin = { CP, TEXT(""), 1, 6, false };
											ParamCache.Add(LaneKey, Pin);
											return Pin;
										};
										FExprPin Acc = MakeMpcScalar(SlotIdx * 4 + SwizzleCharToIndex(Eff[0]));
										for (int32 k = 1; k < Eff.Len(); ++k)
											Acc = CombinePins(Acc, MakeMpcScalar(SlotIdx * 4 + SwizzleCharToIndex(Eff[k])));
										Result = Acc;
									}
									else if (TargetMpc->VectorParameters.IsValidIndex(SlotIdx - ScalarVectors))
									{
										const int32 VecIdx = SlotIdx - ScalarVectors;
										const FString VecKey = FString::Printf(TEXT("MPC|%d|V|%d"), MpcCollectionIdx, VecIdx);
										FExprPin VecPin;
										if (FExprPin* CachedVec = ParamCache.Find(VecKey))
										{
											VecPin = *CachedVec;
										}
										else
										{
											const FCollectionVectorParameter& VP = TargetMpc->VectorParameters[VecIdx];
											UMaterialExpressionCollectionParameter* CP = Spawn<UMaterialExpressionCollectionParameter>(6);
											if (CP)
											{
												CP->Collection = TargetMpc;
												CP->ParameterName = VP.ParameterName;
												CP->ParameterId = VP.Id;
											}
											VecPin = { CP, TEXT(""), 4, 6, false };
											ParamCache.Add(VecKey, VecPin);
										}
										Result = Eff.Equals(TEXT("xyzw")) ? VecPin : ApplyMask(VecPin, Eff, true);
									}
								}

								if (!Result.IsValid())
								{
									if (Eff.Len() == 4 || CurrentOpCode == TEXT("dp4"))
									{
										UMaterialExpressionConstant4Vector* C4 = Spawn<UMaterialExpressionConstant4Vector>(6);
										if (C4) C4->Constant = FLinearColor(0.25f, 0.25f, 0.25f, 0.25f);
										Result = { C4, TEXT(""), 4, 6, false };
									}
									else if (Eff.Len() == 2)
									{
										UMaterialExpressionConstant2Vector* C2 = Spawn<UMaterialExpressionConstant2Vector>(6);
										if (C2) { C2->R = 0.5f; C2->G = 0.5f; }
										Result = { C2, TEXT(""), 2, 6, false };
									}
									else
									{
										Result = MakeConst(1.0f, 6);
									}
								}
								ParamCache.Add(CacheKey, Result);
							}
						}
						else
						{
							// Check if this is Primitive.cb1 in FMeshParticleVertexFactory / single-draw-primitive shaders
							int32 LBr = INDEX_NONE, RBr = INDEX_NONE, PrimSlotIdx = -1;
							if (CbName.Equals(TEXT("cb1")) && !MaterialCbPrefix.Equals(TEXT("cb1")) &&
								Base.FindChar(TEXT('['), LBr) && Base.FindChar(TEXT(']'), RBr) && RBr > LBr)
							{
								PrimSlotIdx = FCString::Atoi(*Base.Mid(LBr + 1, RBr - LBr - 1));
							}
							if ((PrimSlotIdx == 3 || PrimSlotIdx == 5 || PrimSlotIdx == 18) && (Eff.Len() >= 3 || (Eff.Len() == 1 && Eff[0] != TEXT('w'))))
							{
								if (!CachedObjectPos.IsValid())
								{
									UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(8);
									CachedObjectPos = MakeDemotedWS(ObjPos, 7);
								}
								Result = (Eff.Len() == 1) ? ApplyMask(CachedObjectPos, Eff, true) : CachedObjectPos;
							}
							else if (PrimSlotIdx == 5 && Eff == TEXT("w"))
							{
								UMaterialExpressionObjectRadius* ObjRad = Spawn<UMaterialExpressionObjectRadius>(7);
								Result = { ObjRad, TEXT(""), 1, 7, false };
							}
							else if ((PrimSlotIdx >= 0 && PrimSlotIdx <= 2 || PrimSlotIdx >= 6 && PrimSlotIdx <= 8) && Eff.Len() >= 3)
							{
								const bool bWorldToLocal = (PrimSlotIdx >= 6);
								const int32 Axis = bWorldToLocal ? (PrimSlotIdx - 6) : PrimSlotIdx;
								UMaterialExpressionConstant3Vector* Basis = Spawn<UMaterialExpressionConstant3Vector>(8);
								if (Basis)
								{
									Basis->Constant = (Axis == 0) ? FLinearColor(1.0f, 0.0f, 0.0f, 1.0f)
										: (Axis == 1) ? FLinearColor(0.0f, 1.0f, 0.0f, 1.0f)
										: FLinearColor(0.0f, 0.0f, 1.0f, 1.0f);
								}
								UMaterialExpressionTransform* Xf = Spawn<UMaterialExpressionTransform>(7);
								if (Xf)
								{
									Xf->TransformSourceType = bWorldToLocal ? TRANSFORMSOURCE_World : TRANSFORMSOURCE_Local;
									Xf->TransformType = bWorldToLocal ? TRANSFORM_Local : TRANSFORM_World;
									ConnectSmart(Basis, TEXT(""), Xf, TEXT(""));
								}
								FExprPin BasisPin = { Xf ? static_cast<UMaterialExpression*>(Xf) : static_cast<UMaterialExpression*>(Basis), TEXT(""), 3, 7, false };
								Result = ApplyMask(BasisPin, Eff, true);
							}
							else
							{
								Result.bIsBoilerplate = true;
								return Result;
							}
							if (Result.IsValid())
								ParamCache.Add(Base + TEXT("|") + Eff, Result);
						}
					}
					else
					{
						// Resolve Material constant buffer slots using exact CbSlotParamNames & EvaluatedCbSlots when available
						const FString CacheKey = Base + TEXT("|") + Eff;
						if (FExprPin* Cached = ParamCache.Find(CacheKey))
						{
							Result = *Cached;
						}
						else
						{
							int32 LBr = INDEX_NONE, RBr = INDEX_NONE, SlotIdx = 0;
							if (Base.FindChar(TEXT('['), LBr) && Base.FindChar(TEXT(']'), RBr) && RBr > LBr)
								SlotIdx = FCString::Atoi(*Base.Mid(LBr + 1, RBr - LBr - 1));

							const bool bHasWholeName = CbSlotParamNames.Contains(FString::Printf(TEXT("%d"), SlotIdx));
							const bool bHasCompNames =
								CbSlotParamNames.Contains(FString::Printf(TEXT("%d.x"), SlotIdx)) ||
								CbSlotParamNames.Contains(FString::Printf(TEXT("%d.y"), SlotIdx)) ||
								CbSlotParamNames.Contains(FString::Printf(TEXT("%d.z"), SlotIdx)) ||
								CbSlotParamNames.Contains(FString::Printf(TEXT("%d.w"), SlotIdx));
							const bool bIsPackedScalarSlot = (!bHasWholeName && (bHasCompNames || (CbVectorCount > 0 && SlotIdx >= CbVectorCount)));

							auto ResolveMaterialScalarLane = [&](int32 InSlot, int32 CompOff) -> FExprPin
							{
								static const TCHAR CompChars[] = { TEXT('x'), TEXT('y'), TEXT('z'), TEXT('w') };
								const TCHAR CompChar = CompChars[FMath::Clamp(CompOff, 0, 3)];
								const FString LaneKey = FString::Printf(TEXT("MAT|%s|%c"), *Base, CompChar);
								if (FExprPin* CachedLane = ParamCache.Find(LaneKey))
									return *CachedLane;

								FString ExactName;
								if (const FString* FoundComp = CbSlotParamNames.Find(FString::Printf(TEXT("%d.%c"), InSlot, CompChar)))
									ExactName = *FoundComp;
								else if (const FString* FoundWhole = CbSlotParamNames.Find(FString::Printf(TEXT("%d"), InSlot)))
									ExactName = *FoundWhole;

								if (ExactName.Equals(TEXT("SelectionColor"), ESearchCase::IgnoreCase) ||
									ExactName.Equals(TEXT("SelectionColor.a"), ESearchCase::IgnoreCase))
								{
									FExprPin SelPin = { MakeConst(0.0f, 6).Expr, TEXT(""), 1, 6, false, true };
									ParamCache.Add(LaneKey, SelPin);
									return SelPin;
								}

								FExprPin OutScalar;
								if (EvaluatedCbSlots.IsValidIndex(InSlot))
								{
									const FLinearColor& SlotVal = EvaluatedCbSlots[InSlot];
									const float ExactFloat = (CompOff == 1) ? SlotVal.G : (CompOff == 2) ? SlotVal.B : (CompOff == 3) ? SlotVal.A : SlotVal.R;
									FString MatchedName = ExactName;
									if (MatchedName.IsEmpty())
									{
										for (const auto& SP : ScalarParams)
										{
											if (!SP.Key.StartsWith(TEXT("SelectionColor"), ESearchCase::IgnoreCase) &&
												FMath::IsNearlyEqual(SP.Value, ExactFloat, 0.001f))
											{
												MatchedName = SP.Key;
												break;
											}
										}
									}
									if (MatchedName.IsEmpty())
										MatchedName = FString::Printf(TEXT("Param_%d_%d"), InSlot, CompOff);
									UMaterialExpressionScalarParameter* Param = Spawn<UMaterialExpressionScalarParameter>(6);
									if (Param) { Param->ParameterName = FName(*MatchedName); Param->DefaultValue = ExactFloat; }
									OutScalar = { Param, TEXT(""), 1, 6, false, false };
								}
								else if (!ExactName.IsEmpty())
								{
									float DefaultVal = 0.0f;
									for (const auto& SP : ScalarParams)
									{
										if (SP.Key.Equals(ExactName, ESearchCase::IgnoreCase))
										{
											DefaultVal = SP.Value;
											break;
										}
									}
									UMaterialExpressionScalarParameter* Param = Spawn<UMaterialExpressionScalarParameter>(6);
									if (Param) { Param->ParameterName = FName(*ExactName); Param->DefaultValue = DefaultVal; }
									OutScalar = { Param, TEXT(""), 1, 6, false, false };
								}
								else if (ScalarParams.Num() > 0)
								{
									const int32 ScalarBase = (CbVectorCount > 0) ? FMath::Max(0, InSlot - CbVectorCount) : InSlot;
									const int32 ScalarIdx = FMath::Max(0, ScalarBase * 4 + CompOff) % ScalarParams.Num();
									const auto& SP = ScalarParams[ScalarIdx];
									UMaterialExpressionScalarParameter* Param = Spawn<UMaterialExpressionScalarParameter>(6);
									if (Param) { Param->ParameterName = FName(*SP.Key); Param->DefaultValue = SP.Value; }
									OutScalar = { Param, TEXT(""), 1, 6, false, false };
								}
								else
								{
									OutScalar.bIsBoilerplate = true;
									return OutScalar;
								}
								ParamCache.Add(LaneKey, OutScalar);
								return OutScalar;
							};

							auto ResolveMaterialVectorSlot = [&](int32 InSlot) -> FExprPin
							{
								const FString VecKey = FString::Printf(TEXT("MAT|%s|VEC"), *Base);
								if (FExprPin* CachedVec = ParamCache.Find(VecKey))
									return *CachedVec;

								FString ExactName;
								if (const FString* FoundWhole = CbSlotParamNames.Find(FString::Printf(TEXT("%d"), InSlot)))
									ExactName = *FoundWhole;

								if (ExactName.Equals(TEXT("SelectionColor"), ESearchCase::IgnoreCase))
								{
									FExprPin SelPin = { MakeConst(0.0f, 6).Expr, TEXT(""), 3, 6, false, true };
									ParamCache.Add(VecKey, SelPin);
									return SelPin;
								}

								FExprPin OutVec;
								if (EvaluatedCbSlots.IsValidIndex(InSlot))
								{
									const FLinearColor& SlotVal = EvaluatedCbSlots[InSlot];
									FString MatchedName = ExactName;
									if (MatchedName.IsEmpty())
									{
										for (const auto& VP : VectorParams)
										{
											if (!VP.Key.Equals(TEXT("SelectionColor"), ESearchCase::IgnoreCase) &&
												FMath::IsNearlyEqual(VP.Value.R, SlotVal.R, 0.01f) &&
												FMath::IsNearlyEqual(VP.Value.G, SlotVal.G, 0.01f) &&
												FMath::IsNearlyEqual(VP.Value.B, SlotVal.B, 0.01f))
											{
												MatchedName = VP.Key;
												break;
											}
										}
									}
									if (MatchedName.IsEmpty())
										MatchedName = FString::Printf(TEXT("Color_%d"), InSlot);
									UMaterialExpressionVectorParameter* Param = Spawn<UMaterialExpressionVectorParameter>(6);
									if (Param) { Param->ParameterName = FName(*MatchedName); Param->DefaultValue = SlotVal; }
									OutVec = { Param, TEXT("RGB"), 3, 6, false, false };
								}
								else if (!ExactName.IsEmpty())
								{
									FLinearColor DefaultVal = FLinearColor::White;
									for (const auto& VP : VectorParams)
									{
										if (VP.Key.Equals(ExactName, ESearchCase::IgnoreCase))
										{
											DefaultVal = VP.Value;
											break;
										}
									}
									UMaterialExpressionVectorParameter* Param = Spawn<UMaterialExpressionVectorParameter>(6);
									if (Param) { Param->ParameterName = FName(*ExactName); Param->DefaultValue = DefaultVal; }
									OutVec = { Param, TEXT("RGB"), 3, 6, false, false };
								}
								else
								{
									TArray<TPair<FString, FLinearColor>> NonSelVectors;
									for (const auto& VP : VectorParams)
									{
										if (!VP.Key.Equals(TEXT("SelectionColor"), ESearchCase::IgnoreCase))
											NonSelVectors.Add(VP);
									}
									if (NonSelVectors.Num() > 0)
									{
										const auto& VP = NonSelVectors[InSlot % NonSelVectors.Num()];
										UMaterialExpressionVectorParameter* Param = Spawn<UMaterialExpressionVectorParameter>(6);
										if (Param) { Param->ParameterName = FName(*VP.Key); Param->DefaultValue = VP.Value; }
										OutVec = { Param, TEXT("RGB"), 3, 6, false, false };
									}
									else
									{
										OutVec.bIsBoilerplate = true;
										return OutVec;
									}
								}
								ParamCache.Add(VecKey, OutVec);
								return OutVec;
							};

							if (bIsPackedScalarSlot)
							{
								FExprPin Acc = ResolveMaterialScalarLane(SlotIdx, SwizzleCharToIndex(Eff[0]));
								for (int32 k = 1; k < Eff.Len(); ++k)
								{
									FExprPin NextLane = ResolveMaterialScalarLane(SlotIdx, SwizzleCharToIndex(Eff[k]));
									const bool bAnySel = Acc.bIsSelectionColor || NextLane.bIsSelectionColor;
									Acc = CombinePins(Acc, NextLane);
									Acc.bIsSelectionColor = bAnySel;
								}
								Result = Acc;
							}
							else if (Eff.Len() == 1)
							{
								const FString* ExplicitComp = CbSlotParamNames.Find(FString::Printf(TEXT("%d.%c"), SlotIdx, Eff[0]));
								const FString* WholeSlotName = CbSlotParamNames.Find(FString::Printf(TEXT("%d"), SlotIdx));
								const bool bDistinctCompParam = ExplicitComp && (
									ExplicitComp->Equals(TEXT("SelectionColor.a"), ESearchCase::IgnoreCase) ||
									!WholeSlotName || !ExplicitComp->Equals(*WholeSlotName, ESearchCase::IgnoreCase));
								if (bDistinctCompParam)
								{
									Result = ResolveMaterialScalarLane(SlotIdx, SwizzleCharToIndex(Eff[0]));
								}
								else
								{
									FExprPin VecPin = ResolveMaterialVectorSlot(SlotIdx);
									if (VecPin.bIsSelectionColor || VecPin.bIsBoilerplate)
									{
										Result = VecPin;
									}
									else if (Eff[0] == TEXT('w'))
									{
										Result = { VecPin.Expr, TEXT("A"), 1, VecPin.Depth, false, false };
									}
									else
									{
										Result = ApplyMask(VecPin, Eff, true);
									}
								}
							}
							else
							{
								FExprPin VecPin = ResolveMaterialVectorSlot(SlotIdx);
								if (VecPin.bIsSelectionColor || VecPin.bIsBoilerplate)
								{
									Result = VecPin;
								}
								else if (Eff.Equals(TEXT("xyz")))
								{
									Result = VecPin;
								}
								else if (Eff.Equals(TEXT("xyzw")))
								{
									Result = CombinePins(VecPin, { VecPin.Expr, TEXT("A"), 1, VecPin.Depth, false, false });
								}
								else
								{
									Result = ApplyMask(VecPin, Eff, true);
								}
							}

							if (Result.bIsBoilerplate)
								return Result;
							ParamCache.Add(CacheKey, Result);
						}
					}
				}
				else
				{
					ResolveReg(Base, Swizzle, Result);
				}
			}

			if (bAbs && Result.IsValid())
			{
				const int32 D = FMath::Max(0, Result.Depth - 1);
				UMaterialExpressionAbs* AbsNode = Spawn<UMaterialExpressionAbs>(D);
				ConnectSmart(Result.Expr, Result.OutputPin, AbsNode, TEXT(""));
				Result = { AbsNode, TEXT(""), Result.NumChannels, D, false, Result.bIsSelectionColor };
			}
			if (bNegate && Result.IsValid())
			{
				const int32 D = FMath::Max(0, Result.Depth - 1);
				UMaterialExpressionMultiply* NegNode = Spawn<UMaterialExpressionMultiply>(D);
				if (NegNode) NegNode->ConstB = -1.0f;
				ConnectSmart(Result.Expr, Result.OutputPin, NegNode, TEXT("A"));
				Result = { NegNode, TEXT(""), Result.NumChannels, D, false, Result.bIsSelectionColor };
			}
			return Result;
		}

		FExprPin EmitCompareIf(const FCompareCond& Cmp, FExprPin TrueVal, FExprPin FalseVal)
		{
			if (!Cmp.A.IsValid() || !Cmp.B.IsValid() || !TrueVal.IsValid() || !FalseVal.IsValid())
				return FExprPin();
			FExprPin CmpA = (Cmp.A.NumChannels > 1) ? ApplyMask(Cmp.A, TEXT("x"), true) : Cmp.A;
			FExprPin CmpB = (Cmp.B.NumChannels > 1) ? ApplyMask(Cmp.B, TEXT("x"), true) : Cmp.B;
			MatchBinaryChannels(TrueVal, FalseVal);
			const int32 D = FMath::Max(0, FMath::Min3(CmpA.Depth, TrueVal.Depth, FalseVal.Depth) - 1);
			UMaterialExpressionIf* IfNode = Spawn<UMaterialExpressionIf>(D);
			auto ConnectIfPin = [&](const FExprPin& Src, const TCHAR* PinA, const TCHAR* PinB, FExpressionInput* DirectInput)
			{
				if (!Src.IsValid() || !IfNode) return;
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(Src.Expr, Src.OutputPin, IfNode, PinA)) return;
				if (!Src.OutputPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialExpressions(Src.Expr, TEXT(""), IfNode, PinA)) return;
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(Src.Expr, Src.OutputPin, IfNode, PinB)) return;
				if (!Src.OutputPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialExpressions(Src.Expr, TEXT(""), IfNode, PinB)) return;
#if !defined(UEXPLORER_HEADLESS)
				if (DirectInput && !DirectInput->Expression)
				{
					int32 OutIdx = 0;
					if (!Src.OutputPin.IsEmpty())
					{
						const TArray<FExpressionOutput>& Outs = Src.Expr->GetOutputs();
						for (int32 oi = 0; oi < Outs.Num(); ++oi)
						{
							if (Outs[oi].OutputName.ToString().Equals(Src.OutputPin, ESearchCase::IgnoreCase))
							{
								OutIdx = oi;
								break;
							}
						}
					}
					DirectInput->Connect(OutIdx, Src.Expr);
				}
#endif
			};
			ConnectIfPin(CmpA, TEXT("A"), TEXT("A"), IfNode ? &IfNode->A : nullptr);
			ConnectIfPin(CmpB, TEXT("B"), TEXT("B"), IfNode ? &IfNode->B : nullptr);
			if (Cmp.Op == ECompareOp::Ge)
			{
				ConnectIfPin(TrueVal, TEXT("A > B"), TEXT("AGreaterThanB"), IfNode ? &IfNode->AGreaterThanB : nullptr);
				ConnectIfPin(TrueVal, TEXT("A == B"), TEXT("AEqualsB"), IfNode ? &IfNode->AEqualsB : nullptr);
				ConnectIfPin(FalseVal, TEXT("A < B"), TEXT("ALessThanB"), IfNode ? &IfNode->ALessThanB : nullptr);
			}
			else if (Cmp.Op == ECompareOp::Lt)
			{
				ConnectIfPin(FalseVal, TEXT("A > B"), TEXT("AGreaterThanB"), IfNode ? &IfNode->AGreaterThanB : nullptr);
				ConnectIfPin(FalseVal, TEXT("A == B"), TEXT("AEqualsB"), IfNode ? &IfNode->AEqualsB : nullptr);
				ConnectIfPin(TrueVal, TEXT("A < B"), TEXT("ALessThanB"), IfNode ? &IfNode->ALessThanB : nullptr);
			}
			else if (Cmp.Op == ECompareOp::Eq)
			{
				ConnectIfPin(FalseVal, TEXT("A > B"), TEXT("AGreaterThanB"), IfNode ? &IfNode->AGreaterThanB : nullptr);
				ConnectIfPin(TrueVal, TEXT("A == B"), TEXT("AEqualsB"), IfNode ? &IfNode->AEqualsB : nullptr);
				ConnectIfPin(FalseVal, TEXT("A < B"), TEXT("ALessThanB"), IfNode ? &IfNode->ALessThanB : nullptr);
			}
			else // Ne
			{
				ConnectIfPin(TrueVal, TEXT("A > B"), TEXT("AGreaterThanB"), IfNode ? &IfNode->AGreaterThanB : nullptr);
				ConnectIfPin(FalseVal, TEXT("A == B"), TEXT("AEqualsB"), IfNode ? &IfNode->AEqualsB : nullptr);
				ConnectIfPin(TrueVal, TEXT("A < B"), TEXT("ALessThanB"), IfNode ? &IfNode->ALessThanB : nullptr);
			}
			return { IfNode, TEXT(""), FMath::Max(TrueVal.NumChannels, FalseVal.NumChannels), D, false };
		}

		FExprPin ExtractTexSamplePin(UMaterialExpressionTextureSampleParameter2D* TexNode, const FString& DstMask, const FString& TexSwiz, int32 Depth)
		{
			auto PinForCh = [](TCHAR Ch) -> FString
			{
				const TCHAR N = NormalizeSwizzleChar(Ch);
				return (N == TEXT('y')) ? TEXT("G") : (N == TEXT('z')) ? TEXT("B") : (N == TEXT('w')) ? TEXT("A") : TEXT("R");
			};
			const FString ActiveMask = DstMask.IsEmpty() ? TEXT("xyzw") : DstMask;
			const FString EffectiveTexSwiz = (TexSwiz.Len() >= 4) ? TexSwiz : TEXT("xyzw");
			FString SampledSeq;
			for (int32 k = 0; k < ActiveMask.Len(); ++k)
			{
				const int32 DstLane = SwizzleCharToIndex(ActiveMask[k]);
				const TCHAR SrcCh = EffectiveTexSwiz.IsValidIndex(DstLane) ? EffectiveTexSwiz[DstLane] : TEXT("xyzw")[DstLane];
				SampledSeq.AppendChar(NormalizeSwizzleChar(SrcCh));
			}

			if (SampledSeq.Len() == 1)
			{
				return { TexNode, PinForCh(SampledSeq[0]), 1, Depth, false };
			}
			if (SampledSeq.Equals(TEXT("xyz")))
			{
				return { TexNode, TEXT("RGB"), 3, Depth, false };
			}
			if (SampledSeq.Equals(TEXT("xyzw")))
			{
				return CombinePins({ TexNode, TEXT("RGB"), 3, Depth, false }, { TexNode, TEXT("A"), 1, Depth, false });
			}
			if (SampledSeq.Equals(TEXT("xy")))
			{
				return ApplyMask({ TexNode, TEXT("RGB"), 3, Depth, false }, TEXT("xy"), true);
			}
			FExprPin Acc = { TexNode, PinForCh(SampledSeq[0]), 1, Depth, false };
			for (int32 k = 1; k < SampledSeq.Len(); ++k)
			{
				FExprPin NextLane = { TexNode, PinForCh(SampledSeq[k]), 1, Depth, false };
				Acc = CombinePins(Acc, NextLane);
			}
			return Acc;
		}

		void ParseHeaderAndSignature(const TArray<FString>& Lines)
		{
			InputSemantics.Empty();
			InputDynParamIndex.Empty();
			SvPosReg.Empty();
			PrimIdRegs.Empty();

			bool bInInputSig = false;
			int32 MaxCbIndex = 2;
			int32 ParsedViewCb = -1;
			const bool bIsNiagaraSprite = PsVertexFactory.Contains(TEXT("NiagaraSprite")) || PsVertexFactory.Contains(TEXT("ParticleSprite"));
			const bool bIsNiagaraRibbon = PsVertexFactory.Contains(TEXT("NiagaraRibbon"));

			for (const FString& RawLine : Lines)
			{
				FString Line = RawLine.TrimStartAndEnd();
				if (Line.StartsWith(TEXT("ue uniformBuffer View cb=")))
				{
					ParsedViewCb = FCString::Atoi(*Line.Mid(25));
				}
				else if (Line.StartsWith(TEXT("ue uniformBuffer NiagaraMeshVF cb=")))
				{
					NiagaraVfCbPrefix = FString::Printf(TEXT("cb%d"), FCString::Atoi(*Line.Mid(34)));
				}
				else if (Line.StartsWith(TEXT("dcl_constantbuffer CB")))
				{
					int32 Idx = FCString::Atoi(*Line.Mid(21));
					if (Idx > MaxCbIndex) MaxCbIndex = Idx;
				}
				else if (Line.StartsWith(TEXT("dcl_output o3")))
				{
					bIsDeferredGBuffer = true;
				}
				else if (Line.StartsWith(TEXT("dcl_resource_structured t0")))
				{
					FirstMaterialTexSlot = 1;
				}
				else if (Line.Contains(TEXT("Input signature:")))
				{
					bInInputSig = true;
					continue;
				}
				else if (Line.Contains(TEXT("Output signature:")))
				{
					bInInputSig = false;
					continue;
				}

				if (bInInputSig && Line.StartsWith(TEXT("//")))
				{
					FString Body = Line.Mid(2).TrimStartAndEnd();
					TArray<FString> Cols;
					Body.ParseIntoArrayWS(Cols);
					// Format: Name Index Mask Register SysValue Format [Used]
					if (Cols.Num() >= 5 && Cols[3].IsNumeric())
					{
						const FString SemName = Cols[0];
						const int32 SemIdx = FCString::Atoi(*Cols[1]);
						const FString RegName = FString::Printf(TEXT("v%d"), FCString::Atoi(*Cols[3]));

						if (SemName.Equals(TEXT("SV_Position"), ESearchCase::IgnoreCase))
						{
							InputSemantics.Add(RegName, EInputSemantic::SvPosition);
							SvPosReg = RegName;
						}
						else if (SemName.Equals(TEXT("SV_IsFrontFace"), ESearchCase::IgnoreCase))
						{
							InputSemantics.Add(RegName, EInputSemantic::IsFrontFace);
						}
						else if (SemName.Equals(TEXT("PRIMITIVE_ID"), ESearchCase::IgnoreCase))
						{
							InputSemantics.Add(RegName, EInputSemantic::Boilerplate);
							PrimIdRegs.Add(RegName);
						}
						else if ((SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 10) || SemName.Equals(TEXT("TANGENTX")))
						{
							InputSemantics.Add(RegName, EInputSemantic::VertexTangentWS);
						}
						else if ((SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 11) || SemName.Equals(TEXT("TANGENTZ")))
						{
							InputSemantics.Add(RegName, EInputSemantic::VertexNormalWS);
						}
						else if (SemName.Equals(TEXT("PARTICLE_DYNAMIC_PARAM")))
						{
							InputSemantics.Add(RegName, EInputSemantic::DynamicParameter);
							InputDynParamIndex.Add(RegName, SemIdx);
						}
						else if (bIsNiagaraSprite && SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 0)
						{
							InputSemantics.Add(RegName, EInputSemantic::ParticleColor);
						}
						else if (bIsNiagaraSprite && SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 1)
						{
							InputSemantics.Add(RegName, EInputSemantic::TexCoord0);
						}
						else if (bIsNiagaraRibbon && SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 2)
						{
							InputSemantics.Add(RegName, EInputSemantic::ParticleColor);
						}
						else if (bIsNiagaraRibbon && SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 3)
						{
							InputSemantics.Add(RegName, EInputSemantic::TexCoord0);
						}
						else if (SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 0)
						{
							InputSemantics.Add(RegName, EInputSemantic::TexCoord0);
						}
						else if (SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 1)
						{
							InputSemantics.Add(RegName, EInputSemantic::TexCoord2);
						}
						else if (SemName.Equals(TEXT("TEXCOORD")) && SemIdx == 2)
						{
							InputSemantics.Add(RegName, EInputSemantic::TexCoord4);
						}
						else if (SemName.Equals(TEXT("COLOR")) && SemIdx == 0)
						{
							InputSemantics.Add(RegName, EInputSemantic::VertexColor);
						}
						else if (SemName.Equals(TEXT("COLOR")) && SemIdx == 1)
						{
							InputSemantics.Add(RegName, EInputSemantic::ParticleColor);
						}
						else if (SemName.Equals(TEXT("COLOR")) && SemIdx >= 2 && SemIdx <= 5)
						{
							InputSemantics.Add(RegName, EInputSemantic::DynamicParameter);
							InputDynParamIndex.Add(RegName, SemIdx - 2);
						}
						else
						{
							InputSemantics.Add(RegName, EInputSemantic::Boilerplate);
						}
					}
				}
			}

			// Use exact View, Material & MPC CB slot indices from shader info when available
			if (PsViewCbSlot >= 0)
				ViewCbPrefix = FString::Printf(TEXT("cb%d"), PsViewCbSlot);
			else if (ParsedViewCb >= 0)
				ViewCbPrefix = FString::Printf(TEXT("cb%d"), ParsedViewCb);
			else
				ViewCbPrefix = TEXT("cb0");

			if (PsMaterialCbSlot >= 0)
				MaterialCbPrefix = FString::Printf(TEXT("cb%d"), PsMaterialCbSlot);
			else
				MaterialCbPrefix = FString::Printf(TEXT("cb%d"), MaxCbIndex);

			if (PsMpcCbSlot >= 0)
				MpcCbPrefix = FString::Printf(TEXT("cb%d"), PsMpcCbSlot);
		}

		void ParseAndBuild(const FString& Asm)
		{
			PowCondRegs.Empty();
			PowLogBases.Empty();
			CompareConds.Empty();
			PixelDepthCondReg.Empty();
			SvPosRectRegs.Empty();
			PixelDepthRegs.Empty();
			SceneDepthRegs.Empty();
			SceneDepthDiffRegs.Empty();
			SceneColorUvRegs.Empty();
			PrimStructRowOffsets.Empty();
			CachedTexCoords.Empty();
			CachedDynamicParams.Empty();
			CachedVertexColor = nullptr;
			CachedVertexNormalWS = nullptr;
			CachedVertexTangentWS = nullptr;
			CachedTwoSidedSign = nullptr;
			CachedScreenPos = nullptr;
			CachedObjectPos = FExprPin();
			TranslatedWorldPosReg.Empty();
			CameraVecRsqReg.Empty();
			bSawSceneDepthSample = false;
			GraphYOffset = 0;
			bInVertexShader = false;

			TArray<FString> Lines;
			Asm.ParseIntoArrayLines(Lines, true);
			ParseHeaderAndSignature(Lines);

			// 1. Tokenize instructions while stripping UE BasePass boilerplate if_nz branches
			//    (keep the `else` fast-path such as `mov r0.w, v6.w` so Fog registers get marked boilerplate!)
			TArray<FDxbcInstruction> Insts;
			int32 SkipIfDepth = 0;
			bool bInElseBranch = false;

			for (FString Line : Lines)
			{
				Line.TrimStartAndEndInline();
				if (Line.IsEmpty() || Line.StartsWith(TEXT("//")) || Line.StartsWith(TEXT("dcl_")) ||
					Line.StartsWith(TEXT("ps_")) || Line.StartsWith(TEXT("ret")) ||
					Line.StartsWith(TEXT("[")) || Line.StartsWith(TEXT("status=")) ||
					Line.StartsWith(TEXT("target=")) || Line.StartsWith(TEXT("ue ")) ||
					Line.StartsWith(TEXT("(")))
				{
					continue;
				}

				if (Line.StartsWith(TEXT("if_nz")) || Line.StartsWith(TEXT("if_z"))) { SkipIfDepth++; bInElseBranch = false; continue; }
				if (Line.StartsWith(TEXT("else"))) { if (SkipIfDepth == 1) bInElseBranch = true; continue; }
				if (Line.StartsWith(TEXT("endif"))) { if (SkipIfDepth > 0) SkipIfDepth--; if (SkipIfDepth == 0) bInElseBranch = false; continue; }
				if (SkipIfDepth > 0 && !bInElseBranch) continue;

				FDxbcInstruction Inst;
				if (!ParseInstruction(Line, Inst)) continue;
				Insts.Add(MoveTemp(Inst));
			}

			// 2. Walk instructions with exact UE FHLSLMaterialTranslator idiom reversal
			for (int32 i = 0; i < Insts.Num(); ++i)
			{
				const FDxbcInstruction& Inst = Insts[i];
				if (Inst.Args.Num() < 2) continue;
				SetActiveInst(Inst);

				// Global Fix #1: UE SvPosition -> TranslatedWorldPosition -> AbsoluteWorldPosition
				//   add r1.xyzw, r1.xyzw, cb0[47].xyzw
				//   div r1.xyz, r1.xyzx, r1.wwww
				//   add r2.xyz, r1.xyzx, -cb0[70].xyzx  (PreViewTranslation) -> WorldPosition!
				if (Inst.OpCode == TEXT("div") && Inst.Args.Num() >= 3 &&
					Inst.Args[2].EndsWith(TEXT(".wwww")) &&
					i > 0 && Insts[i - 1].OpCode == TEXT("add") && Insts[i - 1].Args.Num() >= 3 &&
					(Insts[i - 1].Args[1].Contains(ViewCbPrefix + TEXT("[47]")) || Insts[i - 1].Args[2].Contains(ViewCbPrefix + TEXT("[47]"))))
				{
					FString Base, Swiz;
					SplitRegSwizzle(Inst.Args[0], Base, Swiz);
					FExprPin Bp; Bp.bIsBoilerplate = true;
					SetReg(Inst.Args[0], Bp);
					TranslatedWorldPosReg = Base;
					continue;
				}
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 &&
					Inst.Args[2].Contains(ViewCbPrefix + TEXT("[70]")))
				{
					// Demote UE5 LWC FWSVector3 WorldPosition -> float3 so SM6 never hits LWC mismatch
					if (!CachedWorldPos) CachedWorldPos = Spawn<UMaterialExpressionWorldPosition>(8);
					FExprPin WPosPin = MakeDemotedWS(CachedWorldPos, 7);
					if (CurrentDstMask.Len() == 1)
					{
						FString B70, S70;
						SplitRegSwizzle(Inst.Args[2], B70, S70);
						FString Eff70 = ComputeEffectiveSwizzle(S70.IsEmpty() ? TEXT("x") : S70, false);
						if (!Eff70.IsEmpty())
							WPosPin = ApplyMask(WPosPin, Eff70.Left(1), true);
					}
					SetReg(Inst.Args[0], WPosPin);
					continue;
				}

				// ScreenPosition: PixelPosition (add dst, vSvPos, -View[129]) & ViewSize AspectRatio (div View[130].x, View[130].y)
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 && !SvPosReg.IsEmpty())
				{
					FString Base1, Sw1, Base2, Sw2;
					SplitRegSwizzle(Inst.Args[1], Base1, Sw1);
					SplitRegSwizzle(Inst.Args[2], Base2, Sw2);
					const bool bSvPos129 =
						(Base1.Equals(SvPosReg, ESearchCase::IgnoreCase) && !Inst.Args[1].StartsWith(TEXT("-")) && Inst.Args[2].Contains(ViewCbPrefix + TEXT("[129]"))) ||
						(Base2.Equals(SvPosReg, ESearchCase::IgnoreCase) && !Inst.Args[2].StartsWith(TEXT("-")) && Inst.Args[1].Contains(ViewCbPrefix + TEXT("[129]")));
					if (bSvPos129)
					{
						if (!CachedScreenPos) CachedScreenPos = SpawnByClassPath(TEXT("/Script/Engine.MaterialExpressionScreenPosition"), 7);
						FExprPin PixPin = { CachedScreenPos, TEXT("PixelPosition"), 2, 7, false };
						if (CurrentDstMask.Len() == 1 && !Inst.Args[1].StartsWith(TEXT("-")))
							PixPin = ApplyMask(PixPin, Sw1.IsEmpty() ? CurrentDstMask : ComputeEffectiveSwizzle(Sw1, false), true);
						SetReg(Inst.Args[0], PixPin);
						AddTrackedReg(SvPosRectRegs, Inst.Args[0]);
						continue;
					}
				}
				if (Inst.OpCode == TEXT("div") && Inst.Args.Num() >= 3 &&
					Inst.Args[1].StartsWith(ViewCbPrefix + TEXT("[130]")) &&
					Inst.Args[2].StartsWith(ViewCbPrefix + TEXT("[130]")))
				{
					SetReg(Inst.Args[0], MakeConst(1.777778f, 7));
					continue;
				}

				// ScreenPosition ViewportUV (mul / mad <SvPosRectReg>, <ViewCb>[130].zw [, offset])
				if ((Inst.OpCode == TEXT("mul") || Inst.OpCode == TEXT("mad")) && Inst.Args.Num() >= 3)
				{
					const bool bArg1Rect = IsTrackedReg(SvPosRectRegs, Inst.Args[1]);
					const bool bArg2Rect = IsTrackedReg(SvPosRectRegs, Inst.Args[2]);
					const bool bArg1Cb130 = Inst.Args[1].StartsWith(ViewCbPrefix + TEXT("[130]"));
					const bool bArg2Cb130 = Inst.Args[2].StartsWith(ViewCbPrefix + TEXT("[130]"));
					if ((bArg1Rect && bArg2Cb130) || (bArg2Rect && bArg1Cb130))
					{
						const FString& RectOp = bArg1Rect ? Inst.Args[1] : Inst.Args[2];
						const FString& CbOp = bArg1Rect ? Inst.Args[2] : Inst.Args[1];
						const bool bNegRect = RectOp.StartsWith(TEXT("-"));
						FString CbBase, CbSwiz;
						SplitRegSwizzle(CbOp, CbBase, CbSwiz);
						FString CbEff = ComputeEffectiveSwizzle(CbSwiz.IsEmpty() ? TEXT("zw") : CbSwiz, false);
						bool bValidInvViewSize = !CbEff.IsEmpty();
						FString UvSwiz;
						for (int32 k = 0; k < CbEff.Len(); ++k)
						{
							if (CbEff[k] == TEXT('z')) UvSwiz.AppendChar(TEXT('x'));
							else if (CbEff[k] == TEXT('w')) UvSwiz.AppendChar(TEXT('y'));
							else { bValidInvViewSize = false; break; }
						}
						if (bValidInvViewSize)
						{
							if (!CachedScreenPos) CachedScreenPos = SpawnByClassPath(TEXT("/Script/Engine.MaterialExpressionScreenPosition"), 7);
							FExprPin UvPin = { CachedScreenPos, TEXT("ViewportUV"), 2, 7, false };
							if (!UvSwiz.Equals(TEXT("xy")))
								UvPin = ApplyMask(UvPin, UvSwiz, true);
							if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4)
							{
								float AddLit = 0.0f;
								if (bNegRect && TryParseSingleLiteral(Inst.Args[3], AddLit) && FMath::IsNearlyEqual(AddLit, 1.0f, 0.001f))
								{
									const int32 Do = FMath::Max(0, UvPin.Depth - 1);
									UMaterialExpressionOneMinus* OneMinus = Spawn<UMaterialExpressionOneMinus>(Do);
									ConnectSmart(UvPin.Expr, UvPin.OutputPin, OneMinus, TEXT(""));
									UvPin = { OneMinus, TEXT(""), UvPin.NumChannels, Do, false };
								}
								else
								{
									if (bNegRect)
									{
										const int32 Dn = FMath::Max(0, UvPin.Depth - 1);
										UMaterialExpressionMultiply* Neg = Spawn<UMaterialExpressionMultiply>(Dn);
										if (Neg) Neg->ConstB = -1.0f;
										ConnectSmart(UvPin.Expr, UvPin.OutputPin, Neg, TEXT("A"));
										UvPin = { Neg, TEXT(""), UvPin.NumChannels, Dn, false };
									}
									FExprPin AddOp = ResolveOperand(Inst.Args[3]);
									if (AddOp.IsValid())
									{
										MatchBinaryChannels(UvPin, AddOp);
										const int32 Da = FMath::Max(0, FMath::Min(UvPin.Depth, AddOp.Depth) - 1);
										UMaterialExpressionAdd* AddN = Spawn<UMaterialExpressionAdd>(Da);
										ConnectSmart(UvPin.Expr, UvPin.OutputPin, AddN, TEXT("A"));
										ConnectSmart(AddOp.Expr, AddOp.OutputPin, AddN, TEXT("B"));
										UvPin = { AddN, TEXT(""), FMath::Max(UvPin.NumChannels, AddOp.NumChannels), Da, false };
									}
								}
							}
							else if (bNegRect)
							{
								const int32 Dn = FMath::Max(0, UvPin.Depth - 1);
								UMaterialExpressionMultiply* Neg = Spawn<UMaterialExpressionMultiply>(Dn);
								if (Neg) Neg->ConstB = -1.0f;
								ConnectSmart(UvPin.Expr, UvPin.OutputPin, Neg, TEXT("A"));
								UvPin = { Neg, TEXT(""), UvPin.NumChannels, Dn, false };
							}
							SetReg(Inst.Args[0], UvPin);
							continue;
						}
					}
				}

				// Clip-space SvPositionToScreenPosition uses vSvPos.w -> propagate Boilerplate
				if (Inst.OpCode == TEXT("mul") && Inst.Args.Num() >= 3 && !SvPosReg.IsEmpty())
				{
					FString Base1, Sw1, Base2, Sw2;
					SplitRegSwizzle(Inst.Args[1], Base1, Sw1);
					SplitRegSwizzle(Inst.Args[2], Base2, Sw2);
					if (Base1.Equals(SvPosReg, ESearchCase::IgnoreCase) || Base2.Equals(SvPosReg, ESearchCase::IgnoreCase))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						continue;
					}
				}

				// Global Fix #2: UE CameraVectorWS = normalize(-TranslatedWorldPosition)
				//   dp3 r1.z, -r1.xyzx, -r1.xyzx
				//   rsq r1.z, r1.z
				//   mad/mul dst, -TWP, Rsq [, Offset]  (or Rsq, -TWP)
				if (Inst.OpCode == TEXT("dp3") && Inst.Args.Num() >= 3 &&
					!TranslatedWorldPosReg.IsEmpty() &&
					Inst.Args[1].StartsWith(FString::Printf(TEXT("-%s"), *TranslatedWorldPosReg)) &&
					i + 1 < Insts.Num() && Insts[i + 1].OpCode == TEXT("rsq"))
				{
					CameraVecRsqReg = Insts[i + 1].Args[0];
					FString RsqMask;
					SplitRegSwizzle(Insts[i + 1].Args[0], CameraVecRsqBase, RsqMask);
					CameraVecRsqLane = RsqMask.IsEmpty() ? TEXT('w') : RsqMask[0];
					i += 1;
					continue;
				}
				if ((Inst.OpCode == TEXT("mad") || Inst.OpCode == TEXT("mul")) && Inst.Args.Num() >= 3 &&
					!TranslatedWorldPosReg.IsEmpty() && !CameraVecRsqReg.IsEmpty())
				{
					const FString NegTwpPrefix = FString::Printf(TEXT("-%s"), *TranslatedWorldPosReg);
					const bool bArg1NegTwp = Inst.Args[1].StartsWith(NegTwpPrefix);
					const bool bArg2NegTwp = Inst.Args[2].StartsWith(NegTwpPrefix);
					const FString& OtherArg = bArg1NegTwp ? Inst.Args[2] : Inst.Args[1];
					FString OtherBase, OtherSwiz;
					SplitRegSwizzle(OtherArg, OtherBase, OtherSwiz);
					const bool bOtherIsRsq = OtherArg.StartsWith(CameraVecRsqReg) ||
						(OtherBase.Equals(CameraVecRsqBase) && (OtherSwiz.IsEmpty() || OtherSwiz.Contains(FString::Chr(CameraVecRsqLane))));
					if ((bArg1NegTwp || bArg2NegTwp) && bOtherIsRsq)
					{
						if (!CachedCameraVec) CachedCameraVec = Spawn<UMaterialExpressionCameraVectorWS>(7);
						FExprPin CamPin = { CachedCameraVec, TEXT(""), 3, 7, false };
						const FString& TwpOp = bArg1NegTwp ? Inst.Args[1] : Inst.Args[2];
						FString TwpBase, TwpSwiz;
						SplitRegSwizzle(TwpOp.Mid(1), TwpBase, TwpSwiz);
						FString TwpEff = ComputeEffectiveSwizzle(TwpSwiz, false);
						if (!TwpEff.IsEmpty() && !TwpEff.Equals(TEXT("xyz")))
							CamPin = ApplyMask(CamPin, TwpEff, true);
						else if (CurrentDstMask.Len() > 0 && CurrentDstMask.Len() < 3)
							CamPin = ApplyMask(CamPin, CurrentDstMask, true);

						if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4)
						{
							FExprPin Off = ResolveOperand(Inst.Args[3]);
							if (Off.IsValid())
							{
								MatchBinaryChannels(CamPin, Off);
								UMaterialExpressionAdd* AddNode = Spawn<UMaterialExpressionAdd>(6);
								ConnectSmart(CamPin.Expr, CamPin.OutputPin, AddNode, TEXT("A"));
								ConnectSmart(Off.Expr, Off.OutputPin, AddNode, TEXT("B"));
								CamPin = { AddNode, TEXT(""), FMath::Max(CamPin.NumChannels, Off.NumChannels), 6, false };
							}
						}
						SetReg(Inst.Args[0], CamPin);
						TranslatedWorldPosReg.Empty();
						CameraVecRsqReg.Empty();
						CameraVecRsqBase.Empty();
						continue;
					}
				}

				// PS Primitive structured buffer indexing (LocalToWorld basis / ObjectPositionWS / DeterminantSign)
				if (Inst.OpCode == TEXT("imul") && Inst.Args.Num() >= 4 && Inst.Args[0].Equals(TEXT("null"), ESearchCase::IgnoreCase))
				{
					FString SrcBase, SrcSwiz;
					SplitRegSwizzle(Inst.Args[2], SrcBase, SrcSwiz);
					if (PrimIdRegs.Contains(SrcBase))
					{
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[1], DstBase, DstMask);
						if (!DstBase.IsEmpty())
						{
							if (DstMask.IsEmpty())
								PrimStructRowOffsets.Add(DstBase, 0);
							else
								for (int32 k = 0; k < DstMask.Len(); ++k)
									PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), 0);
						}
					}
					continue;
				}
				if (Inst.OpCode == TEXT("imad") && Inst.Args.Num() >= 4)
				{
					FString SrcBase, SrcSwiz;
					SplitRegSwizzle(Inst.Args[1], SrcBase, SrcSwiz);
					if (PrimIdRegs.Contains(SrcBase))
					{
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						FString Trim = Inst.Args[3].TrimStartAndEnd();
						TArray<int32> OffVals;
						if (Trim.StartsWith(TEXT("l(")) && Trim.EndsWith(TEXT(")")))
						{
							TArray<FString> Parts;
							Trim.Mid(2, Trim.Len() - 3).ParseIntoArray(Parts, TEXT(","), true);
							for (const FString& P : Parts) OffVals.Add(FCString::Atoi(*P.TrimStartAndEnd()));
						}
						if (OffVals.Num() == 4 && !DstMask.IsEmpty() && !DstMask.Equals(TEXT("xyzw")))
						{
							for (int32 k = 0; k < DstMask.Len(); ++k)
							{
								const int32 LaneIdx = SwizzleCharToIndex(DstMask[k]);
								PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), OffVals.IsValidIndex(LaneIdx) ? OffVals[LaneIdx] : 0);
							}
						}
						else if (OffVals.Num() >= 1)
						{
							if (DstMask.IsEmpty())
								PrimStructRowOffsets.Add(DstBase, OffVals[0]);
							else
								for (int32 k = 0; k < DstMask.Len(); ++k)
									PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), OffVals[FMath::Min(k, OffVals.Num() - 1)]);
						}
					}
					continue;
				}
				if (Inst.OpCode == TEXT("ld_structured_indexable") && Inst.Args.Num() >= 4)
				{
					FString IdxBase, IdxSwiz;
					SplitRegSwizzle(Inst.Args[1], IdxBase, IdxSwiz);
					FString IdxEff = ComputeEffectiveSwizzle(IdxSwiz, true);
					int32 RowOff = -1;
					if (const int32* ExactOff = PrimStructRowOffsets.Find(Inst.Args[1])) RowOff = *ExactOff;
					else if (!IdxEff.IsEmpty())
					{
						if (const int32* LaneOff = PrimStructRowOffsets.Find(FString::Printf(TEXT("%s.%c"), *IdxBase, IdxEff[0])))
							RowOff = *LaneOff;
					}
					if (RowOff < 0)
					{
						if (const int32* BaseOff = PrimStructRowOffsets.Find(IdxBase)) RowOff = *BaseOff;
					}

					int32 ByteOffComp = 0;
					{
						FString ByteArg = Inst.Args[2].TrimStartAndEnd();
						if (ByteArg.StartsWith(TEXT("l(")) && ByteArg.EndsWith(TEXT(")")))
							ByteOffComp = FMath::Clamp(FCString::Atoi(*ByteArg.Mid(2, ByteArg.Len() - 3)) / 4, 0, 3);
					}
					{
						FString LDstBase, LDstMask;
						SplitRegSwizzle(Inst.Args[0], LDstBase, LDstMask);
						const FString ClrMask = LDstMask.IsEmpty() ? FString(TEXT("xyzw")) : LDstMask;
						for (int32 ci = 0; ci < ClrMask.Len(); ++ci)
							PrimStructRowOffsets.Remove(FString::Printf(TEXT("%s.%c"), *LDstBase, ClrMask[ci]));
					}

					if ((RowOff >= 0 && RowOff <= 2) || (RowOff >= 6 && RowOff <= 8))
					{
						const int32 Axis = (RowOff >= 6) ? (RowOff - 6) : RowOff;
						UMaterialExpressionConstant3Vector* Basis = Spawn<UMaterialExpressionConstant3Vector>(8);
						if (Basis)
							Basis->Constant = (Axis == 0) ? FLinearColor(1, 0, 0, 1) : (Axis == 1) ? FLinearColor(0, 1, 0, 1) : FLinearColor(0, 0, 1, 1);
						UMaterialExpressionTransform* Xf = Spawn<UMaterialExpressionTransform>(7);
						if (Xf)
						{
							Xf->TransformSourceType = (RowOff >= 6) ? TRANSFORMSOURCE_World : TRANSFORMSOURCE_Local;
							Xf->TransformType = (RowOff >= 6) ? TRANSFORM_Local : TRANSFORM_World;
							ConnectSmart(Basis, TEXT(""), Xf, TEXT(""));
						}
						FExprPin BasisPin = { Xf ? static_cast<UMaterialExpression*>(Xf) : static_cast<UMaterialExpression*>(Basis), TEXT(""), 3, 7, false };
						if (!CurrentDstMask.IsEmpty() && CurrentDstMask.Len() < 3) BasisPin = ApplyMask(BasisPin, CurrentDstMask, true);
						SetReg(Inst.Args[0], BasisPin);
					}
					else if (RowOff == 3 || RowOff == 5 || RowOff == 18)
					{
						if (ByteOffComp == 3 && RowOff == 5)
						{
							UMaterialExpressionObjectRadius* ObjRad = Spawn<UMaterialExpressionObjectRadius>(7);
							SetReg(Inst.Args[0], { ObjRad, TEXT(""), 1, 7, false });
						}
						else
						{
							if (!CachedObjectPos.IsValid())
							{
								UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(8);
								CachedObjectPos = MakeDemotedWS(ObjPos, 7);
							}
							FExprPin ObjPin = CachedObjectPos;
							if (ByteOffComp > 0 && ByteOffComp < 3)
							{
								const TCHAR CompCh[2] = { TEXT("xyz")[ByteOffComp], 0 };
								ObjPin = ApplyMask(ObjPin, CompCh, true);
							}
							else if (CurrentDstMask.Len() == 1)
							{
								ObjPin = ApplyMask(ObjPin, CurrentDstMask, true);
							}
							SetReg(Inst.Args[0], ObjPin);
						}
					}
					else if (RowOff == 4 || CurrentDstMask.Len() == 1)
					{
						SetReg(Inst.Args[0], MakeConst(1.0f, 7));
					}
					else if (CurrentDstMask.Len() >= 3)
					{
						if (!CachedObjectPos.IsValid())
						{
							UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(8);
							CachedObjectPos = MakeDemotedWS(ObjPos, 7);
						}
						SetReg(Inst.Args[0], CachedObjectPos);
					}
					else
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
					}
					continue;
				}
				if (Inst.OpCode == TEXT("ld_indexable") && Inst.Args.Num() >= 3)
				{
					FExprPin Bp; Bp.bIsBoilerplate = true;
					SetReg(Inst.Args[0], Bp);
					continue;
				}

				// Idiom 1: UE FHLSLMaterialTranslator::DynamicParameter -> GetDynamicParameter(Parameters.Particle, Default, Index)
				//   and rT, NiagaraVF.DynamicParamMask, l(1)
				//   movc rT, rT, vColor2.<x|y|z|w>, l(DefaultVal)
				if (Inst.OpCode == TEXT("and") && Inst.Args.Num() >= 3 && i + 1 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("movc") && Insts[i + 1].Args.Num() >= 4 &&
					!CurrentDstMask.Equals(TEXT("xyzw")))
				{
					FString DynBase, DynSwizzle;
					SplitRegSwizzle(Insts[i + 1].Args[2], DynBase, DynSwizzle);
					if (const EInputSemantic* Sem = InputSemantics.Find(DynBase); Sem && *Sem == EInputSemantic::DynamicParameter)
					{
						const FString NextCond = Insts[i + 1].Args[1];
						const FString AndDst = Inst.Args[0];
						FString AndBase, AndMask, NextCondBase, NextCondSwiz;
						SplitRegSwizzle(AndDst, AndBase, AndMask);
						SplitRegSwizzle(NextCond, NextCondBase, NextCondSwiz);
						if (AndBase.Equals(NextCondBase))
						{
							const int32* ParamIdxPtr = InputDynParamIndex.Find(DynBase);
							const int32 ParamIdx = ParamIdxPtr ? *ParamIdxPtr : 0;
							float DefaultVal = 1.0f;
							TryParseSingleLiteral(Insts[i + 1].Args[3], DefaultVal);

							UMaterialExpressionDynamicParameter* DP = GetOrSpawnDynamicParam(ParamIdx);
							int32 DynIdx = 0;
							if (DynSwizzle.StartsWith(TEXT("y"))) DynIdx = 1;
							else if (DynSwizzle.StartsWith(TEXT("z"))) DynIdx = 2;
							else if (DynSwizzle.StartsWith(TEXT("w"))) DynIdx = 3;

							if (DP)
							{
								if (DynIdx == 0) DP->DefaultValue.R = DefaultVal;
								else if (DynIdx == 1) DP->DefaultValue.G = DefaultVal;
								else if (DynIdx == 2) DP->DefaultValue.B = DefaultVal;
								else if (DynIdx == 3) DP->DefaultValue.A = DefaultVal;
							}
							const FString PinName = DynamicParamPinForLane(DP, DynIdx);
							SetActiveInst(Insts[i + 1]);
							SetReg(Insts[i + 1].Args[0], { DP, PinName, 1, 4, false });
							i += 1;
							continue;
						}
					}
				}

				// Idiom 2: UE FHLSLMaterialTranslator::Power -> PositiveClampedPow(Base, Exp)
				//   [optional max X, X, l(0.0)]
				//   ge rT, l(0.0), X ; log Y, X ; mul Y, Y, Exp ; exp Y, Y ; ... ; movc Dst, rT, l(0), ...
				if (Inst.OpCode == TEXT("max") && Inst.Args.Num() >= 3 && i + 1 < Insts.Num())
				{
					float ZeroLit = 1.0f;
					if (TryParseSingleLiteral(Inst.Args[2], ZeroLit) && FMath::IsNearlyZero(ZeroLit))
					{
						bool bFeedsPow = false;
						if (i + 4 < Insts.Num() &&
							Insts[i + 1].OpCode == TEXT("ge") &&
							Insts[i + 2].OpCode == TEXT("log") &&
							Insts[i + 3].OpCode == TEXT("mul") &&
							Insts[i + 4].OpCode == TEXT("exp"))
						{
							FString MaxDstBase, MaxDstSwiz;
							SplitRegSwizzle(Inst.Args[0], MaxDstBase, MaxDstSwiz);
							FString LogBase, LogSwiz;
							SplitRegSwizzle(Insts[i + 2].Args.Num() >= 2 ? Insts[i + 2].Args[1] : TEXT(""), LogBase, LogSwiz);
							if (MaxDstBase.Equals(LogBase, ESearchCase::IgnoreCase))
								bFeedsPow = true;
						}
						if (bFeedsPow)
						{
							FExprPin PassPin = ResolveOperand(Inst.Args[1]);
							if (PassPin.IsValid())
								SetReg(Inst.Args[0], PassPin);
							continue;
						}
					}
				}

				auto EmitPowFromBaseAndExpArg = [&](const FExprPin& BasePin, const FString& ExpArg, bool bSaturate) -> FExprPin
				{
					FExprPin ExpPin;
					float ExpLit = 0.0f;
					const bool bLitExp = TryParseSingleLiteral(ExpArg, ExpLit);
					if (!bLitExp)
						ExpPin = ResolveOperand(ExpArg);

					const int32 D = FMath::Max(1, BasePin.Depth - 1);
					UMaterialExpressionPower* PowNode = Spawn<UMaterialExpressionPower>(D);
					if (PowNode)
					{
						ConnectSmart(BasePin.Expr, BasePin.OutputPin, PowNode, TEXT("Base"));
						if (bLitExp)
							PowNode->ConstExponent = ExpLit;
						else if (ExpPin.IsValid())
							ConnectSmart(ExpPin.Expr, ExpPin.OutputPin, PowNode, TEXT("Exp"));
					}
					const int32 OutCh = FMath::Max(BasePin.NumChannels, (!bLitExp && ExpPin.IsValid()) ? ExpPin.NumChannels : 1);
					FExprPin PowPin = { PowNode, TEXT(""), OutCh, D, false };
					if (bSaturate && PowPin.IsValid())
					{
						const int32 Ds = FMath::Max(0, D - 1);
						UMaterialExpressionSaturate* Sat = Spawn<UMaterialExpressionSaturate>(Ds);
						ConnectSmart(PowPin.Expr, PowPin.OutputPin, Sat, TEXT(""));
						PowPin = { Sat, TEXT(""), PowPin.NumChannels, Ds, false };
					}
					return PowPin;
				};

				if (Inst.OpCode == TEXT("ge") && i + 3 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("log") &&
					Insts[i + 2].OpCode == TEXT("mul") &&
					Insts[i + 3].OpCode == TEXT("exp"))
				{
					SetActiveInst(Insts[i + 1]);
					FExprPin BasePin = ResolveOperand(Insts[i + 1].Args[1]);

					SetActiveInst(Insts[i + 2]);
					FString LogDstBase, LogDstMask, Mul1Base, Mul1Swiz;
					SplitRegSwizzle(Insts[i + 1].Args[0], LogDstBase, LogDstMask);
					SplitRegSwizzle(Insts[i + 2].Args[1], Mul1Base, Mul1Swiz);
					FString Mul1Eff = ComputeEffectiveSwizzle(Mul1Swiz, false);
					const bool bArg1IsLog = !LogDstBase.IsEmpty() && Mul1Base.Equals(LogDstBase) &&
						(LogDstMask.IsEmpty() || Mul1Eff.IsEmpty() || LogDstMask.Contains(Mul1Eff.Left(1)));
					const FString& ExpArg = bArg1IsLog ? Insts[i + 2].Args[2] : Insts[i + 2].Args[1];

					if (BasePin.IsValid())
					{
						FExprPin PowPin = EmitPowFromBaseAndExpArg(BasePin, ExpArg, Insts[i + 3].bSaturate);
						SetActiveInst(Insts[i + 3]);
						SetReg(Insts[i + 3].Args[0], PowPin);
						FString ExpDstBase, ExpDstMask;
						SplitRegSwizzle(Insts[i + 3].Args[0], ExpDstBase, ExpDstMask);
						if (!LogDstBase.IsEmpty() &&
							(!LogDstBase.Equals(ExpDstBase) || (!LogDstMask.IsEmpty() && !ExpDstMask.IsEmpty() && !MasksOverlap(LogDstMask, ExpDstMask))))
						{
							PowLogBases.Add(Insts[i + 1].Args[0], BasePin);
						}
						FString CondBase, CondMask;
						SplitRegSwizzle(Inst.Args[0], CondBase, CondMask);
						if (CondMask.IsEmpty())
						{
							PowCondRegs.Add(CondBase);
						}
						else
						{
							for (int32 ci = 0; ci < CondMask.Len(); ++ci)
								PowCondRegs.Add(FString::Printf(TEXT("%s.%c"), *CondBase, CondMask[ci]));
						}
						i += 3;
						continue;
					}
				}

				// Idiom 2b: Secondary pow(Base, Exp2) reusing a shared log2(Base) register from Idiom 2
				if (Inst.OpCode == TEXT("mul") && Inst.Args.Num() >= 3 && i + 1 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("exp") && Insts[i + 1].Args.Num() >= 2 && PowLogBases.Num() > 0)
				{
					const FExprPin* LogBase1 = PowLogBases.Find(Inst.Args[1]);
					const FExprPin* LogBase2 = LogBase1 ? nullptr : PowLogBases.Find(Inst.Args[2]);
					if (LogBase1 || LogBase2)
					{
						FString MulDstBase, MulDstMask, ExpSrcBase, ExpSrcSwiz;
						SplitRegSwizzle(Inst.Args[0], MulDstBase, MulDstMask);
						SplitRegSwizzle(Insts[i + 1].Args[1], ExpSrcBase, ExpSrcSwiz);
						if (MulDstBase.Equals(ExpSrcBase))
						{
							const FExprPin SavedBase = *(LogBase1 ? LogBase1 : LogBase2);
							const FString& ExpArg = LogBase1 ? Inst.Args[2] : Inst.Args[1];
							SetActiveInst(Inst);
							FExprPin PowPin = EmitPowFromBaseAndExpArg(SavedBase, ExpArg, Insts[i + 1].bSaturate);
							SetActiveInst(Insts[i + 1]);
							SetReg(Insts[i + 1].Args[0], PowPin);
							i += 1;
							continue;
						}
					}
				}

				// Idiom 3: UE SelectionColor overlay lerp stripping
				//   Pattern A: add rDiff, -X, cbMat[Sel] ; mad Dst, cbMat[SelA], rDiff, X
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 && i + 1 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("mad") && Insts[i + 1].Args.Num() >= 4)
				{
					bool bIsSelColor = (CbSlotParamNames.Num() == 0 && VectorParams.Num() == 0 &&
						Inst.Args[2].StartsWith(MaterialCbPrefix + TEXT("[")) &&
						Insts[i + 1].Args[1].StartsWith(MaterialCbPrefix + TEXT("[")));
					if (!bIsSelColor)
					{
						if (Inst.Args[2].StartsWith(MaterialCbPrefix + TEXT("[")))
						{
							SetActiveInst(Inst);
							if (ResolveOperand(Inst.Args[2]).bIsSelectionColor) bIsSelColor = true;
						}
						if (!bIsSelColor && Inst.Args[1].StartsWith(MaterialCbPrefix + TEXT("[")))
						{
							SetActiveInst(Inst);
							if (ResolveOperand(Inst.Args[1]).bIsSelectionColor) bIsSelColor = true;
						}
						if (!bIsSelColor && Insts[i + 1].Args[1].StartsWith(MaterialCbPrefix + TEXT("[")))
						{
							SetActiveInst(Insts[i + 1]);
							if (ResolveOperand(Insts[i + 1].Args[1]).bIsSelectionColor) bIsSelColor = true;
						}
					}
					if (bIsSelColor)
					{
						SetActiveInst(Insts[i + 1]);
						FExprPin BaseX = ResolveOperand(Insts[i + 1].Args[3]);
						if (BaseX.IsValid())
							SetReg(Insts[i + 1].Args[0], BaseX);
						i += 1;
						continue;
					}
					SetActiveInst(Inst);
				}

				//   Pattern B: mad rDiff, -X, S, cbMat[Sel] ; mad Dst, cbMat[SelA], rDiff, XScaled
				if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4 && i + 1 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("mad") && Insts[i + 1].Args.Num() >= 4)
				{
					bool bIsSelColor = false;
					if (Inst.Args[3].StartsWith(MaterialCbPrefix + TEXT("[")))
					{
						SetActiveInst(Inst);
						if (ResolveOperand(Inst.Args[3]).bIsSelectionColor) bIsSelColor = true;
					}
					if (!bIsSelColor && Insts[i + 1].Args[1].StartsWith(MaterialCbPrefix + TEXT("[")))
					{
						SetActiveInst(Insts[i + 1]);
						if (ResolveOperand(Insts[i + 1].Args[1]).bIsSelectionColor) bIsSelColor = true;
					}
					if (bIsSelColor)
					{
						SetActiveInst(Insts[i + 1]);
						FExprPin BaseX = ResolveOperand(Insts[i + 1].Args[3]);
						if (BaseX.IsValid())
							SetReg(Insts[i + 1].Args[0], BaseX);
						i += 1;
						continue;
					}
					SetActiveInst(Inst);
				}

				// Idiom 3b: General UE FHLSLMaterialTranslator::Lerp -> lerp(A, B, Alpha):
				//   add rDiff, B, -A  (or add rDiff, -A, B)
				//   mad Dst, Alpha, rDiff, A
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 && i + 1 < Insts.Num() &&
					Insts[i + 1].OpCode == TEXT("mad") && Insts[i + 1].Args.Num() >= 4)
				{
					FString DiffDstBase, DiffDstMask;
					SplitRegSwizzle(Inst.Args[0], DiffDstBase, DiffDstMask);
					FString MadSrc2Base, MadSrc2Mask, MadDstBase, MadDstMask;
					SplitRegSwizzle(Insts[i + 1].Args[2], MadSrc2Base, MadSrc2Mask);
					SplitRegSwizzle(Insts[i + 1].Args[0], MadDstBase, MadDstMask);
					const bool bNegA = Inst.Args[1].StartsWith(TEXT("-")) || Inst.Args[2].StartsWith(TEXT("-"));
					if (bNegA && DiffDstBase.Equals(MadSrc2Base) && DiffDstMask.Equals(MadDstMask))
					{
						const FString& NegArg = Inst.Args[1].StartsWith(TEXT("-")) ? Inst.Args[1] : Inst.Args[2];
						const FString& RawB = Inst.Args[1].StartsWith(TEXT("-")) ? Inst.Args[2] : Inst.Args[1];
						FString NegBase, NegSwiz, ABase, ASwiz;
						SplitRegSwizzle(NegArg.Mid(1), NegBase, NegSwiz);
						SplitRegSwizzle(Insts[i + 1].Args[3], ABase, ASwiz);
						if (NegBase.Equals(ABase))
						{
							SetActiveInst(Inst);
							FExprPin PinB = ResolveOperand(RawB);
							SetActiveInst(Insts[i + 1]);
							FExprPin PinA = ResolveOperand(Insts[i + 1].Args[3]);
							FExprPin PinAlpha = ResolveOperand(Insts[i + 1].Args[1]);

							if (PinA.bIsSelectionColor || PinB.bIsSelectionColor || PinAlpha.bIsSelectionColor)
							{
								if (PinA.IsValid()) SetReg(Insts[i + 1].Args[0], PinA);
								i += 1;
								continue;
							}
							if ((PinA.bIsBoilerplate && PinB.bIsBoilerplate) ||
								((PinA.bIsBoilerplate || PinB.bIsBoilerplate) && Inst.Args[1].Contains(ViewCbPrefix + TEXT("["))))
							{
								const bool bLerpSceneDepth = IsTrackedReg(SceneDepthRegs, RawB) ||
									IsTrackedReg(SceneDepthRegs, Insts[i + 1].Args[3]);
								FExprPin Bp; Bp.bIsBoilerplate = true;
								SetReg(Insts[i + 1].Args[0], Bp);
								if (bLerpSceneDepth) AddTrackedReg(SceneDepthRegs, Insts[i + 1].Args[0]);
								i += 1;
								continue;
							}
							if (PinA.IsValid() && PinB.IsValid() && PinAlpha.IsValid())
						{
							MatchBinaryChannels(PinA, PinB);
							const int32 D = FMath::Max(0, FMath::Min3(PinA.Depth, PinB.Depth, PinAlpha.Depth) - 1);
							UMaterialExpressionLinearInterpolate* LerpNode = Spawn<UMaterialExpressionLinearInterpolate>(D);
							ConnectSmart(PinA.Expr, PinA.OutputPin, LerpNode, TEXT("A"));
							ConnectSmart(PinB.Expr, PinB.OutputPin, LerpNode, TEXT("B"));
							ConnectSmart(PinAlpha.Expr, PinAlpha.OutputPin, LerpNode, TEXT("Alpha"));
							FExprPin LerpPin = { LerpNode, TEXT(""), FMath::Max(PinA.NumChannels, PinB.NumChannels), D, false };
							if (Insts[i + 1].bSaturate && LerpPin.IsValid())
							{
								const int32 Ds = FMath::Max(0, D - 1);
								UMaterialExpressionSaturate* Sat = Spawn<UMaterialExpressionSaturate>(Ds);
								ConnectSmart(LerpPin.Expr, LerpPin.OutputPin, Sat, TEXT(""));
								LerpPin = { Sat, TEXT(""), LerpPin.NumChannels, Ds, false };
							}
								SetReg(Insts[i + 1].Args[0], LerpPin);
								i += 1;
								continue;
							}
						}
					}
				}

				// Idiom 4: Additive/Translucent Opacity capture (mov_sat rN.w, src) - strictly alpha lane .w!
				if (Inst.OpCode == TEXT("mov") && Inst.bSaturate && Inst.Args.Num() >= 2 && CurrentDstMask.Equals(TEXT("w")))
				{
					FExprPin OpacSrc = ResolveOperand(Inst.Args[1]);
					if (OpacSrc.IsValid())
					{
						CapturedOpacityPin = OpacSrc;
						SetReg(Inst.Args[0], OpacSrc);
						continue;
					}
				}

				// Idiom 5: mad dst, -|src|, l(M), l(1.0) -> OneMinus(Multiply(Abs(src), M))
				if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4)
				{
					float MulLit = 0.0f, AddLit = 0.0f;
					if (Inst.Args[1].StartsWith(TEXT("-")) &&
						TryParseSingleLiteral(Inst.Args[2], MulLit) &&
						TryParseSingleLiteral(Inst.Args[3], AddLit) &&
						FMath::IsNearlyEqual(AddLit, 1.0f, 0.001f))
					{
						FExprPin PosSrc = ResolveOperand(Inst.Args[1].Mid(1));
						if (PosSrc.IsValid())
						{
							FExprPin Scaled = PosSrc;
							if (!FMath::IsNearlyEqual(MulLit, 1.0f, 0.001f))
							{
								const int32 Dm = FMath::Max(1, PosSrc.Depth - 1);
								UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(Dm);
								if (MulNode)
								{
									MulNode->ConstB = MulLit;
									ConnectSmart(PosSrc.Expr, PosSrc.OutputPin, MulNode, TEXT("A"));
									Scaled = { MulNode, TEXT(""), PosSrc.NumChannels, Dm, false };
								}
							}
							const int32 Do = FMath::Max(0, Scaled.Depth - 1);
							UMaterialExpressionOneMinus* OneMinus = Spawn<UMaterialExpressionOneMinus>(Do);
							ConnectSmart(Scaled.Expr, Scaled.OutputPin, OneMinus, TEXT(""));
							SetReg(Inst.Args[0], { OneMinus, TEXT(""), Scaled.NumChannels, Do, false });
							continue;
						}
					}
				}

				// DepthFade difference: add dst, SceneDepth, -PixelDepth
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3)
				{
					const bool bSD1 = IsTrackedReg(SceneDepthRegs, Inst.Args[1]);
					const bool bPD1 = IsTrackedReg(PixelDepthRegs, Inst.Args[1]);
					const bool bSD2 = IsTrackedReg(SceneDepthRegs, Inst.Args[2]);
					const bool bPD2 = IsTrackedReg(PixelDepthRegs, Inst.Args[2]);
					if ((bSD1 && bPD2) || (bPD1 && bSD2))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						AddTrackedReg(SceneDepthDiffRegs, Inst.Args[0]);
						continue;
					}
				}

				// Idiom 6: add dst, src, l(-C) -> Subtract(src, C)
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3)
				{
					float LitB = 0.0f;
					if (TryParseSingleLiteral(Inst.Args[2], LitB) && LitB < 0.0f)
					{
						FExprPin A = ResolveOperand(Inst.Args[1]);
						if (A.IsValid())
						{
							const int32 D = FMath::Max(0, A.Depth - 1);
							UMaterialExpressionSubtract* Sub = Spawn<UMaterialExpressionSubtract>(D);
							if (Sub)
							{
								Sub->ConstB = -LitB;
								ConnectSmart(A.Expr, A.OutputPin, Sub, TEXT("A"));
								SetReg(Inst.Args[0], { Sub, TEXT(""), A.NumChannels, D, false });
							}
							continue;
						}
					}
				}

				// Idiom 7: DepthFade reconstruction (div_sat or reciprocal mul_sat after SceneDepth - PixelDepth)
				if ((Inst.OpCode == TEXT("div") || Inst.OpCode == TEXT("mul")) && Inst.bSaturate && Inst.Args.Num() >= 3)
				{
					const bool bArg1Diff = IsTrackedReg(SceneDepthDiffRegs, Inst.Args[1]) ||
						(Inst.OpCode == TEXT("div") && SceneDepthDiffRegs.Num() == 0 && bSawSceneDepthSample && IsTrackedReg(SceneDepthRegs, Inst.Args[1]));
					const bool bArg2Diff = (Inst.OpCode == TEXT("mul")) && IsTrackedReg(SceneDepthDiffRegs, Inst.Args[2]);
					if (bArg1Diff || bArg2Diff)
					{
						const FString& FadeArg = bArg1Diff ? Inst.Args[2] : Inst.Args[1];
						UMaterialExpressionDepthFade* DF = Spawn<UMaterialExpressionDepthFade>(5);
						if (DF) DF->OpacityDefault = 1.0f;
						if (Inst.OpCode == TEXT("div"))
						{
							FExprPin FadeDist = ResolveOperand(FadeArg);
							if (DF && FadeDist.IsValid())
								ConnectSmart(FadeDist.Expr, FadeDist.OutputPin, DF, TEXT("FadeDistance"));
						}
						else
						{
							float InvLit = 0.0f;
							if (TryParseSingleLiteral(FadeArg, InvLit) && FMath::Abs(InvLit) > 1e-6f)
							{
								if (DF) DF->FadeDistanceDefault = 1.0f / InvLit;
							}
							else
							{
								FExprPin InvPin = ResolveOperand(FadeArg);
								if (DF && InvPin.IsValid())
								{
									UMaterialExpressionDivide* Recip = Spawn<UMaterialExpressionDivide>(6);
									if (Recip) Recip->ConstA = 1.0f;
									ConnectSmart(InvPin.Expr, InvPin.OutputPin, Recip, TEXT("B"));
									ConnectSmart(Recip, TEXT(""), DF, TEXT("FadeDistance"));
								}
							}
						}
						SetReg(Inst.Args[0], { DF, TEXT(""), 1, 5, false });
						continue;
					}
				}

				// Compare instructions (lt / ge / eq / ne) saved for subsequent movc or and
				if ((Inst.OpCode == TEXT("lt") || Inst.OpCode == TEXT("ge") || Inst.OpCode == TEXT("eq") || Inst.OpCode == TEXT("ne")) && Inst.Args.Num() >= 3)
				{
					if (Inst.OpCode == TEXT("lt") && Inst.Args[1].Contains(ViewCbPrefix + TEXT("[31]")))
					{
						PixelDepthCondReg = Inst.Args[0];
						continue;
					}
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					if (A.IsValid() && B.IsValid())
					{
						const ECompareOp Op = (Inst.OpCode == TEXT("lt")) ? ECompareOp::Lt
							: (Inst.OpCode == TEXT("eq")) ? ECompareOp::Eq
							: (Inst.OpCode == TEXT("ne")) ? ECompareOp::Ne
							: ECompareOp::Ge;
						FString CmpBase, CmpMask;
						SplitRegSwizzle(Inst.Args[0], CmpBase, CmpMask);
						SetReg(Inst.Args[0], FExprPin());
						FCompareCond Cmp{ A, B, Op };
						CompareConds.Add(Inst.Args[0], Cmp);
						if (CmpMask.Len() > 1)
						{
							for (int32 k = 0; k < CmpMask.Len(); ++k)
							{
								const TCHAR LaneCh = CmpMask[k];
								const TCHAR SrcCh[2] = { TEXT("xyzw")[FMath::Min(k, 3)], 0 };
								FExprPin ALane = (A.NumChannels > 1) ? ApplyMask(A, SrcCh, true) : A;
								FExprPin BLane = (B.NumChannels > 1) ? ApplyMask(B, SrcCh, true) : B;
								CompareConds.Add(FString::Printf(TEXT("%s.%c"), *CmpBase, LaneCh), FCompareCond{ ALane, BLane, Op });
							}
						}
					}
					continue;
				}

				// Standard instructions
				FExprPin OutPin;
				if (Inst.OpCode == TEXT("and") && Inst.Args.Num() >= 3)
				{
					auto FindCompare = [&](const FString& Op) -> const FCompareCond*
					{
						if (const FCompareCond* P = CompareConds.Find(Op)) return P;
						FString B, S; SplitRegSwizzle(Op, B, S);
						FString Eff = ComputeEffectiveSwizzle(S, false);
						const FString EffKey = Eff.IsEmpty() ? B : (B + TEXT(".") + Eff);
						if (const FCompareCond* P = CompareConds.Find(EffKey)) return P;
						if (Eff.Len() >= 1)
						{
							if (const FCompareCond* P1 = CompareConds.Find(FString::Printf(TEXT("%s.%c"), *B, Eff[0]))) return P1;
						}
						return CompareConds.Find(B);
					};
					if (const FCompareCond* CmpPtr = FindCompare(Inst.Args[2]))
					{
						FCompareCond CmpCopy = *CmpPtr;
						FExprPin Val = ResolveOperand(Inst.Args[1]);
						if (Val.IsValid())
							OutPin = EmitCompareIf(CmpCopy, Val, MakeConst(0.0f, Val.Depth));
						else if (const FCompareCond* CmpPtr2 = FindCompare(Inst.Args[1]))
						{
							FCompareCond CmpCopy2 = *CmpPtr2;
							FExprPin Val2 = ResolveOperand(Inst.Args[2]);
							if (Val2.IsValid())
								OutPin = EmitCompareIf(CmpCopy2, Val2, MakeConst(0.0f, Val2.Depth));
						}
					}
					else if (const FCompareCond* CmpPtr2 = FindCompare(Inst.Args[1]))
					{
						FCompareCond CmpCopy = *CmpPtr2;
						FExprPin Val = ResolveOperand(Inst.Args[2]);
						if (Val.IsValid())
							OutPin = EmitCompareIf(CmpCopy, Val, MakeConst(0.0f, Val.Depth));
					}
					else
					{
						OutPin.bIsBoilerplate = true;
					}
				}
				else if (Inst.OpCode == TEXT("movc") && Inst.Args.Num() >= 4)
				{
					FString CondBase, CondSwiz;
					SplitRegSwizzle(Inst.Args[1], CondBase, CondSwiz);
					FString CondEff = ComputeEffectiveSwizzle(CondSwiz, false);
					const FString CondEffKey = CondEff.IsEmpty() ? CondBase : (CondBase + TEXT(".") + CondEff);

					// PixelDepth (lt rFlag, View[31].w, 1.0 ... movc dst, rFlag, rSceneW, rLinearZ)
					if (!PixelDepthCondReg.IsEmpty() &&
						(Inst.Args[1].Equals(PixelDepthCondReg) || CondEffKey.Equals(PixelDepthCondReg) || CondBase.Equals(PixelDepthCondReg)))
					{
						PixelDepthCondReg.Empty();
						UMaterialExpressionPixelDepth* PD = Spawn<UMaterialExpressionPixelDepth>(7);
						OutPin = { PD, TEXT(""), 1, 7, false };
						SetReg(Inst.Args[0], OutPin);
						AddTrackedReg(PixelDepthRegs, Inst.Args[0]);
						continue;
					}
					else if (const EInputSemantic* CondSem = InputSemantics.Find(CondBase); CondSem && *CondSem == EInputSemantic::IsFrontFace)
					{
						float TVal = 0.0f, FVal = 0.0f;
						const bool bTLit = TryParseSingleLiteral(Inst.Args[2], TVal);
						const bool bFLit = TryParseSingleLiteral(Inst.Args[3], FVal);
						if (!CachedTwoSidedSign) CachedTwoSidedSign = Spawn<UMaterialExpressionTwoSidedSign>(7);
						FExprPin SignPin = { CachedTwoSidedSign, TEXT(""), 1, 7, false };
						if (bTLit && bFLit && FMath::IsNearlyEqual(TVal, 1.0f, 0.001f) && FMath::IsNearlyEqual(FVal, -1.0f, 0.001f))
						{
							OutPin = SignPin;
						}
						else if (bTLit && bFLit && FMath::IsNearlyEqual(TVal, -1.0f, 0.001f) && FMath::IsNearlyEqual(FVal, 1.0f, 0.001f))
						{
							UMaterialExpressionMultiply* NegSign = Spawn<UMaterialExpressionMultiply>(6);
							if (NegSign) NegSign->ConstB = -1.0f;
							ConnectSmart(SignPin.Expr, SignPin.OutputPin, NegSign, TEXT("A"));
							OutPin = { NegSign, TEXT(""), 1, 6, false };
						}
						else
						{
							FExprPin TrueVal = ResolveOperand(Inst.Args[2]);
							FExprPin FalseVal = ResolveOperand(Inst.Args[3]);
							if (TrueVal.IsValid() && FalseVal.IsValid())
								OutPin = EmitCompareIf({ SignPin, MakeConst(0.0f, 7), ECompareOp::Ge }, TrueVal, FalseVal);
							else if (TrueVal.IsValid())
								OutPin = TrueVal;
							else if (FalseVal.IsValid())
								OutPin = FalseVal;
						}
					}
					else
					{
						if (PowCondRegs.Contains(Inst.Args[1]) || PowCondRegs.Contains(CondEffKey) || PowCondRegs.Contains(CondBase))
						{
							FExprPin FalseVal = ResolveOperand(Inst.Args[3]);
							if (FalseVal.bIsBoilerplate) { SetReg(Inst.Args[0], FalseVal); continue; }
							if (FalseVal.IsValid()) OutPin = FalseVal;
						}
						else
						{
							auto FindCmpLane = [&](const FString& Key) -> FCompareCond*
							{
								if (FCompareCond* P = CompareConds.Find(Key)) return P;
								return nullptr;
							};
							FCompareCond* CmpPtr = FindCmpLane(Inst.Args[1]);
							if (!CmpPtr) CmpPtr = FindCmpLane(CondEffKey);
							if (!CmpPtr && CondEff.Len() >= 1)
								CmpPtr = FindCmpLane(FString::Printf(TEXT("%s.%c"), *CondBase, CondEff[0]));
							if (!CmpPtr) CmpPtr = FindCmpLane(CondBase);
							FCompareCond CmpCopy;
							const bool bHasCmp = (CmpPtr != nullptr);
							if (bHasCmp) CmpCopy = *CmpPtr;

							FExprPin TrueVal = ResolveOperand(Inst.Args[2]);
							FExprPin FalseVal = ResolveOperand(Inst.Args[3]);
							if (bHasCmp && TrueVal.IsValid() && FalseVal.IsValid())
							{
								bool bMultiLaneCmp = false;
								if (CurrentDstMask.Len() > 1 && CondEff.Len() >= CurrentDstMask.Len())
								{
									for (int32 li = 1; li < CurrentDstMask.Len(); ++li)
									{
										if (CondEff[li] != CondEff[0]) { bMultiLaneCmp = true; break; }
									}
								}
								if (bMultiLaneCmp)
								{
									FExprPin Acc;
									for (int32 li = 0; li < CurrentDstMask.Len(); ++li)
									{
										FCompareCond* LaneCmp = FindCmpLane(FString::Printf(TEXT("%s.%c"), *CondBase, CondEff[li]));
										if (!LaneCmp) LaneCmp = &CmpCopy;
										const TCHAR LaneCh[2] = { TEXT("xyzw")[FMath::Min(li, 3)], 0 };
										FExprPin TL = (TrueVal.NumChannels > 1) ? ApplyMask(TrueVal, LaneCh, true) : TrueVal;
										FExprPin FL = (FalseVal.NumChannels > 1) ? ApplyMask(FalseVal, LaneCh, true) : FalseVal;
										FExprPin ResL = EmitCompareIf(*LaneCmp, TL, FL);
										Acc = (li == 0) ? ResL : CombinePins(Acc, ResL);
									}
									OutPin = Acc;
								}
								else
								{
									OutPin = EmitCompareIf(CmpCopy, TrueVal, FalseVal);
								}
							}
							else if (IsAllZeroLit(Inst.Args[2]) && FalseVal.IsValid())
							{
								OutPin = FalseVal;
							}
							else if (TrueVal.IsValid())
							{
								OutPin = TrueVal;
							}
							else if (FalseVal.IsValid())
							{
								OutPin = FalseVal;
							}
							else if (TrueVal.bIsBoilerplate || FalseVal.bIsBoilerplate)
							{
								FExprPin Bp; Bp.bIsBoilerplate = true;
								SetReg(Inst.Args[0], Bp);
								continue;
							}
						}
					}
				}
				else if (Inst.OpCode == TEXT("mov") && Inst.Args.Num() >= 2)
				{
					// Skip UE Additive boilerplate `mov o0.w, l(0)` while preserving Translucent `mov o0.w, rX.w`
					if (Inst.Args[0].Equals(TEXT("o0.w"), ESearchCase::IgnoreCase) && IsAllZeroLit(Inst.Args[1]))
						continue;
					OutPin = ResolveOperand(Inst.Args[1]);
					if (OutPin.bIsBoilerplate)
					{
						SetReg(Inst.Args[0], OutPin);
						continue;
					}
					if (Inst.Args[0].Equals(TEXT("o0.w"), ESearchCase::IgnoreCase) && OutPin.IsValid())
						CapturedOpacityPin = OutPin;
				}
				else if ((Inst.OpCode == TEXT("add") || Inst.OpCode == TEXT("mul") || Inst.OpCode == TEXT("div") ||
					Inst.OpCode == TEXT("min") || Inst.OpCode == TEXT("max")) && Inst.Args.Num() >= 3)
				{
					const bool bInSceneDepth = IsTrackedReg(SceneDepthRegs, Inst.Args[1]) || IsTrackedReg(SceneDepthRegs, Inst.Args[2]);
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);

					// Propagate engine boilerplate (ScreenPosition/SceneDepth math vs live Fog/PreExposure passthrough)
					if ((A.bIsBoilerplate && B.bIsBoilerplate) ||
						(A.bIsBoilerplate && !B.IsValid()) ||
						(B.bIsBoilerplate && !A.IsValid()) ||
						(Inst.OpCode == TEXT("div") && (A.bIsBoilerplate || B.bIsBoilerplate)) ||
						(bInSceneDepth && (A.bIsBoilerplate || B.bIsBoilerplate)) ||
						(A.bIsBoilerplate && Inst.Args[2].StartsWith(TEXT("l("))) ||
						(B.bIsBoilerplate && Inst.Args[1].StartsWith(TEXT("l("))))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						if (bInSceneDepth) AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
						continue;
					}
					if (A.bIsBoilerplate && B.IsValid()) { SetReg(Inst.Args[0], B); continue; }
					if (B.bIsBoilerplate && A.IsValid()) { SetReg(Inst.Args[0], A); continue; }
					if (!A.IsValid() || !B.IsValid()) continue;

					// Strip Forward Translucent spherical-harmonic diffuse lobe (mul r7, rSH, l(0.886228))
					float ShMulLit = 0.0f;
					if (Inst.OpCode == TEXT("mul") &&
						((Inst.Args[2].StartsWith(TEXT("l(")) && TryParseSingleLiteral(Inst.Args[2], ShMulLit) && FMath::IsNearlyEqual(FMath::Abs(ShMulLit), 0.886228f, 0.001f)) ||
						 (Inst.Args[1].StartsWith(TEXT("l(")) && TryParseSingleLiteral(Inst.Args[1], ShMulLit) && FMath::IsNearlyEqual(FMath::Abs(ShMulLit), 0.886228f, 0.001f))))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						continue;
					}

					// Capture & strip BasePassPS Additive Opacity premultiply right before output o0
					if (Inst.OpCode == TEXT("mul") && CurrentDstMask.Equals(TEXT("xyz")) &&
						Material->BlendMode == BLEND_Additive &&
						i + 1 < Insts.Num() && Insts[i + 1].Args.Num() >= 2 && Insts[i + 1].Args[0].StartsWith(TEXT("o0")))
					{
						if (A.NumChannels == 1 && B.NumChannels >= 3) { CapturedOpacityPin = A; SetReg(Inst.Args[0], B); continue; }
						if (B.NumChannels == 1 && A.NumChannels >= 3) { CapturedOpacityPin = B; SetReg(Inst.Args[0], A); continue; }
					}

					// Skip BasePassPS Additive Opacity premultiply (r0.xyz = Opacity * Emissive) so Emissive & Opacity stay cleanly separated!
					if (Inst.OpCode == TEXT("mul") && Material->BlendMode == BLEND_Additive && CapturedOpacityPin.IsValid() &&
						(A.Expr == CapturedOpacityPin.Expr && A.OutputPin == CapturedOpacityPin.OutputPin))
					{
						SetReg(Inst.Args[0], B);
						continue;
					}
					if (Inst.OpCode == TEXT("mul") && Material->BlendMode == BLEND_Additive && CapturedOpacityPin.IsValid() &&
						(B.Expr == CapturedOpacityPin.Expr && B.OutputPin == CapturedOpacityPin.OutputPin))
					{
						SetReg(Inst.Args[0], A);
						continue;
					}

					MatchBinaryChannels(A, B);
					const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
					UMaterialExpression* Node = nullptr;
					if (Inst.OpCode == TEXT("add")) Node = Spawn<UMaterialExpressionAdd>(D);
					else if (Inst.OpCode == TEXT("mul")) Node = Spawn<UMaterialExpressionMultiply>(D);
					else if (Inst.OpCode == TEXT("div")) Node = Spawn<UMaterialExpressionDivide>(D);
					else if (Inst.OpCode == TEXT("min")) Node = Spawn<UMaterialExpressionMin>(D);
					else Node = Spawn<UMaterialExpressionMax>(D);
					ConnectSmart(A.Expr, A.OutputPin, Node, TEXT("A"));
					ConnectSmart(B.Expr, B.OutputPin, Node, TEXT("B"));
					OutPin = { Node, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
				}
				else if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4)
				{
					const bool bInSceneDepth = IsTrackedReg(SceneDepthRegs, Inst.Args[1]) ||
						IsTrackedReg(SceneDepthRegs, Inst.Args[2]) ||
						IsTrackedReg(SceneDepthRegs, Inst.Args[3]);
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					FExprPin C = ResolveOperand(Inst.Args[3]);

					const bool bALit = Inst.Args[1].StartsWith(TEXT("l("));
					const bool bBLit = Inst.Args[2].StartsWith(TEXT("l("));
					const bool bACb0 = Inst.Args[1].Contains(ViewCbPrefix + TEXT("["));
					const bool bBCb0 = Inst.Args[2].Contains(ViewCbPrefix + TEXT("["));
					// Capture tangent-space normal at View[138] (NormalOverrideParameter: xyz=0, w=1)
					if (Inst.Args[3].Contains(ViewCbPrefix + TEXT("[138]")) && A.IsValid() && B.IsValid())
					{
						FExprPin NormPin;
						if (Inst.Args[1].Contains(ViewCbPrefix + TEXT("[138]")) && !B.bIsBoilerplate)
						{
							NormPin = B;
						}
						else if (Inst.Args[2].Contains(ViewCbPrefix + TEXT("[138]")) && !A.bIsBoilerplate)
						{
							NormPin = A;
						}
						else if (!A.bIsBoilerplate && !B.bIsBoilerplate)
						{
							MatchBinaryChannels(A, B);
							const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
							UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(D);
							ConnectSmart(A.Expr, A.OutputPin, MulNode, TEXT("A"));
							ConnectSmart(B.Expr, B.OutputPin, MulNode, TEXT("B"));
							NormPin = { MulNode, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
						}
						if (NormPin.IsValid())
						{
							if (!CapturedNormalPin.IsValid())
							{
								CapturedNormalPin = NormPin;
							}
							SetReg(Inst.Args[0], NormPin);
							continue;
						}
					}
					const bool bIsView139 = Inst.Args[1].Contains(ViewCbPrefix + TEXT("[139]")) || Inst.Args[2].Contains(ViewCbPrefix + TEXT("[139]"));
					if (bIsView139)
					{
						if (A.IsValid() && B.bIsBoilerplate && (C.bIsBoilerplate || !C.IsValid())) { SetReg(Inst.Args[0], A); continue; }
						if (B.IsValid() && A.bIsBoilerplate && (C.bIsBoilerplate || !C.IsValid())) { SetReg(Inst.Args[0], B); continue; }
					}
					const bool bIsView145 = Inst.Args[1].Contains(ViewCbPrefix + TEXT("[145]")) || Inst.Args[2].Contains(ViewCbPrefix + TEXT("[145]"));
					if (bIsView145)
					{
						if (bIsDeferredGBuffer && Material->BlendMode != BLEND_Translucent && Material->BlendMode != BLEND_Additive &&
							!Material->GetShadingModels().HasShadingModel(MSM_Unlit) && C.IsValid() && !C.bIsBoilerplate)
						{
							SetReg(Inst.Args[0], C);
						}
						else
						{
							FExprPin Bp; Bp.bIsBoilerplate = true;
							SetReg(Inst.Args[0], Bp);
						}
						continue;
					}
					// Convert ScreenPositionScaleBias (cb0[66]) Clip UV [-1,1] -> ViewportUV [0,1] for SceneColor
					if (Inst.Args[2].Contains(ViewCbPrefix + TEXT("[66]")) && Inst.Args[3].Contains(ViewCbPrefix + TEXT("[66]")) &&
						A.IsValid() && !A.bIsBoilerplate)
					{
						const int32 D = FMath::Max(0, A.Depth - 2);
						UMaterialExpressionConstant2Vector* ScaleC2 = Spawn<UMaterialExpressionConstant2Vector>(D + 2);
						if (ScaleC2) { ScaleC2->R = 0.5f; ScaleC2->G = -0.5f; }
						UMaterialExpressionConstant2Vector* BiasC2 = Spawn<UMaterialExpressionConstant2Vector>(D + 1);
						if (BiasC2) { BiasC2->R = 0.5f; BiasC2->G = 0.5f; }
						UMaterialExpressionMultiply* MulN = Spawn<UMaterialExpressionMultiply>(D + 1);
						ConnectSmart(A.Expr, A.OutputPin, MulN, TEXT("A"));
						ConnectSmart(ScaleC2, TEXT(""), MulN, TEXT("B"));
						UMaterialExpressionAdd* AddN = Spawn<UMaterialExpressionAdd>(D);
						ConnectSmart(MulN, TEXT(""), AddN, TEXT("A"));
						ConnectSmart(BiasC2, TEXT(""), AddN, TEXT("B"));
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						SceneColorUvRegs.Add(DstBase);
						SetReg(Inst.Args[0], { AddN, TEXT(""), 2, D, false });
						continue;
					}
					if ((bACb0 || bBCb0) && (bALit || bBLit || (A.bIsBoilerplate && B.bIsBoilerplate)) && C.bIsBoilerplate)
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						if (bInSceneDepth) AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
						continue;
					}
					if ((bACb0 || bBCb0) && (bALit || bBLit) && C.IsValid() &&
						(Inst.Args[1].Contains(TEXT("[136]")) || Inst.Args[2].Contains(TEXT("[136]")) ||
						 Inst.Args[1].Contains(TEXT("[137]")) || Inst.Args[2].Contains(TEXT("[137]"))))
					{
						SetReg(Inst.Args[0], C);
						continue;
					}
					// Strip Forward Translucent spherical-harmonic diffuse lobe (mul rSH, rBaseColor, vInterpolator; mul r7, rSH, l(0.886228))
					float ShLit = 0.0f;
					if (bBLit && TryParseSingleLiteral(Inst.Args[2], ShLit) && FMath::IsNearlyEqual(FMath::Abs(ShLit), 0.886228f, 0.001f))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						continue;
					}
					if ((bACb0 || bBCb0) && bInSceneDepth)
					{
						if (A.bIsBoilerplate && B.bIsBoilerplate && C.IsValid())
						{
							SetReg(Inst.Args[0], C);
							continue;
						}
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
						continue;
					}
					if (bInSceneDepth)
					{
						if ((A.bIsBoilerplate || B.bIsBoilerplate) && C.bIsBoilerplate)
						{
							FExprPin Bp; Bp.bIsBoilerplate = true;
							SetReg(Inst.Args[0], Bp);
							AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
							continue;
						}
						if (C.bIsBoilerplate && A.IsValid() && B.IsValid())
						{
							MatchBinaryChannels(A, B);
							const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
							UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(D);
							ConnectSmart(A.Expr, A.OutputPin, MulNode, TEXT("A"));
							ConnectSmart(B.Expr, B.OutputPin, MulNode, TEXT("B"));
							OutPin = { MulNode, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
							SetReg(Inst.Args[0], OutPin);
							continue;
						}
					}
					if (A.IsValid() && !bALit && B.bIsBoilerplate && (C.bIsBoilerplate || !C.IsValid()))
					{
						SetReg(Inst.Args[0], A);
						continue;
					}
					if (B.IsValid() && !bBLit && A.bIsBoilerplate && (C.bIsBoilerplate || !C.IsValid()))
					{
						SetReg(Inst.Args[0], B);
						continue;
					}
					if ((A.bIsBoilerplate || B.bIsBoilerplate) && (C.bIsBoilerplate || !C.IsValid() || Inst.Args[3].StartsWith(TEXT("l("))))
					{
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						if (bInSceneDepth) AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
						continue;
					}
					if ((A.bIsBoilerplate || B.bIsBoilerplate) && C.IsValid())
					{
						SetReg(Inst.Args[0], C);
						continue;
					}
					if (A.IsValid() && B.IsValid())
					{
						MatchBinaryChannels(A, B);
						const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
						UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(D);
						ConnectSmart(A.Expr, A.OutputPin, MulNode, TEXT("A"));
						ConnectSmart(B.Expr, B.OutputPin, MulNode, TEXT("B"));
						FExprPin Prod = { MulNode, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
						if (C.IsValid() && !C.bIsBoilerplate)
						{
							MatchBinaryChannels(Prod, C);
							const int32 D2 = FMath::Max(0, FMath::Min(Prod.Depth, C.Depth) - 1);
							UMaterialExpressionAdd* AddNode = Spawn<UMaterialExpressionAdd>(D2);
							ConnectSmart(Prod.Expr, Prod.OutputPin, AddNode, TEXT("A"));
							ConnectSmart(C.Expr, C.OutputPin, AddNode, TEXT("B"));
							OutPin = { AddNode, TEXT(""), FMath::Max(Prod.NumChannels, C.NumChannels), D2, false };
						}
						else
						{
							OutPin = Prod;
						}
					}
					else if (C.IsValid() && !C.bIsBoilerplate)
					{
						OutPin = C;
					}
				}
				else if ((Inst.OpCode == TEXT("dp2") || Inst.OpCode == TEXT("dp3") || Inst.OpCode == TEXT("dp4")) && Inst.Args.Num() >= 3)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					if (Inst.OpCode == TEXT("dp3"))
					{
						if (A.bIsBoilerplate && B.IsValid() && B.NumChannels >= 3)
						{
							if (!CachedVertexNormalWS) CachedVertexNormalWS = Spawn<UMaterialExpressionVertexNormalWS>(7);
							A = { CachedVertexNormalWS, TEXT(""), 3, 7, false };
						}
						else if (B.bIsBoilerplate && A.IsValid() && A.NumChannels >= 3)
						{
							if (!CachedVertexNormalWS) CachedVertexNormalWS = Spawn<UMaterialExpressionVertexNormalWS>(7);
							B = { CachedVertexNormalWS, TEXT(""), 3, 7, false };
						}
					}
					if (A.IsValid() && B.IsValid())
					{
						MatchBinaryChannels(A, B);
						const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
						UMaterialExpressionDotProduct* Dot = Spawn<UMaterialExpressionDotProduct>(D);
						ConnectSmart(A.Expr, A.OutputPin, Dot, TEXT("A"));
						ConnectSmart(B.Expr, B.OutputPin, Dot, TEXT("B"));
						OutPin = { Dot, TEXT(""), 1, D, false };
					}
					else if (A.bIsBoilerplate || B.bIsBoilerplate)
					{
						OutPin.bIsBoilerplate = true;
					}
				}
				else if ((Inst.OpCode == TEXT("sqrt") || Inst.OpCode == TEXT("rsq") || Inst.OpCode == TEXT("exp") ||
					Inst.OpCode == TEXT("log") || Inst.OpCode == TEXT("frc") ||
					Inst.OpCode == TEXT("round_ni") || Inst.OpCode == TEXT("round_pi") || Inst.OpCode == TEXT("round_ne")) && Inst.Args.Num() >= 2)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					if (A.IsValid())
					{
						const int32 D = FMath::Max(0, A.Depth - 1);
						if (Inst.OpCode == TEXT("rsq"))
						{
							UMaterialExpressionSquareRoot* Sq = Spawn<UMaterialExpressionSquareRoot>(D);
							ConnectSmart(A.Expr, A.OutputPin, Sq, TEXT(""));
							const int32 D2 = FMath::Max(0, D - 1);
							UMaterialExpressionDivide* Div = Spawn<UMaterialExpressionDivide>(D2);
							if (Div) Div->ConstA = 1.0f;
							ConnectSmart(Sq, TEXT(""), Div, TEXT("B"));
							OutPin = { Div, TEXT(""), A.NumChannels, D2, false };
						}
						else if (Inst.OpCode == TEXT("exp"))
						{
							FExprPin Two = MakeConst(2.0f, D + 1);
							UMaterialExpressionPower* Pow = Spawn<UMaterialExpressionPower>(D);
							ConnectSmart(Two.Expr, Two.OutputPin, Pow, TEXT("Base"));
							ConnectSmart(A.Expr, A.OutputPin, Pow, TEXT("Exp"));
							OutPin = { Pow, TEXT(""), A.NumChannels, D, false };
						}
						else if (Inst.OpCode == TEXT("log"))
						{
							UMaterialExpressionLogarithm2* Log2Node = Spawn<UMaterialExpressionLogarithm2>(D);
							ConnectSmart(A.Expr, A.OutputPin, Log2Node, TEXT(""));
							OutPin = { Log2Node, TEXT(""), A.NumChannels, D, false };
						}
						else if (Inst.OpCode == TEXT("round_ne"))
						{
							UMaterialExpressionRound* Rnd = Spawn<UMaterialExpressionRound>(D);
							ConnectSmart(A.Expr, A.OutputPin, Rnd, TEXT(""));
							OutPin = { Rnd, TEXT(""), A.NumChannels, D, false };
						}
						else
						{
							UMaterialExpression* Node = nullptr;
							if (Inst.OpCode == TEXT("sqrt")) Node = Spawn<UMaterialExpressionSquareRoot>(D);
							else if (Inst.OpCode == TEXT("frc")) Node = Spawn<UMaterialExpressionFrac>(D);
							else if (Inst.OpCode == TEXT("round_ni")) Node = Spawn<UMaterialExpressionFloor>(D);
							else Node = Spawn<UMaterialExpressionCeil>(D);
							ConnectSmart(A.Expr, A.OutputPin, Node, TEXT(""));
							OutPin = { Node, TEXT(""), A.NumChannels, D, false };
						}
					}
				}
				else if (Inst.OpCode == TEXT("sincos") && Inst.Args.Num() >= 3)
				{
					FExprPin Angle = ResolveOperand(Inst.Args[2]);
					if (Angle.IsValid())
					{
						const int32 D = FMath::Max(0, Angle.Depth - 1);
						if (!Inst.Args[0].Equals(TEXT("null"), ESearchCase::IgnoreCase))
						{
							UMaterialExpressionSine* SinNode = Spawn<UMaterialExpressionSine>(D);
							if (SinNode) SinNode->Period = UE_TWO_PI;
							ConnectSmart(Angle.Expr, Angle.OutputPin, SinNode, TEXT(""));
							SetReg(Inst.Args[0], { SinNode, TEXT(""), Angle.NumChannels, D, false });
						}
						if (!Inst.Args[1].Equals(TEXT("null"), ESearchCase::IgnoreCase))
						{
							UMaterialExpressionCosine* CosNode = Spawn<UMaterialExpressionCosine>(D);
							if (CosNode) CosNode->Period = UE_TWO_PI;
							ConnectSmart(Angle.Expr, Angle.OutputPin, CosNode, TEXT(""));
							SetReg(Inst.Args[1], { CosNode, TEXT(""), Angle.NumChannels, D, false });
						}
						continue;
					}
				}
				else if ((Inst.OpCode == TEXT("discard_nz") || Inst.OpCode == TEXT("discard_z")) && Inst.Args.Num() >= 1)
				{
					FExprPin MaskPin = ResolveOperand(Inst.Args[0]);
					if (MaskPin.IsValid())
						CapturedOpacityMaskPin = MaskPin;
					continue;
				}
				else if (Inst.OpCode.StartsWith(TEXT("sample")) && Inst.Args.Num() >= 3)
				{
					FExprPin UV = ResolveOperand(Inst.Args[1]);
					if (UV.bIsBoilerplate || Inst.bIsTex3D)
					{
						bSawSceneDepthSample = true;
						FExprPin Bp; Bp.bIsBoilerplate = true;
						SetReg(Inst.Args[0], Bp);
						AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
						continue;
					}
					FString TexReg = Inst.Args[2];
					FString TexBase, TexSwiz;
					SplitRegSwizzle(TexReg, TexBase, TexSwiz);

					// When exact PsTextureSlots mapping is available, skip non-material textures (e.g. SceneDepth t1)
					UTexture2D* ResolvedTex = nullptr;
					FString ResolvedTexName;
					FString ResolvedParamName;
					if (PsTextureSlots.Num() > 0)
					{
						if (const FString* ExactName = PsTextureSlots.Find(TexBase))
						{
							ResolvedTexName = *ExactName;
							if (UTexture2D** Found = TextureByName.Find(*ExactName))
								ResolvedTex = *Found;
							if (const FString* ParamName = PsTextureParamNames.Find(TexBase); ParamName && !ParamName->IsEmpty())
								ResolvedParamName = *ParamName;
						}
						else
						{
							FString UvBase, UvSwiz;
							SplitRegSwizzle(Inst.Args[1], UvBase, UvSwiz);
							if (Inst.OpCode == TEXT("sample_indexable") &&
								!UV.bIsBoilerplate && UV.IsValid() && SceneColorUvRegs.Contains(UvBase))
							{
								const int32 D = FMath::Max(1, UV.Depth - 1);
								UMaterialExpressionSceneColor* ScNode = Spawn<UMaterialExpressionSceneColor>(D);
								if (ScNode)
								{
									ScNode->InputMode = EMaterialSceneAttributeInputMode::Coordinates;
									ConnectSmart(UV.Expr, UV.OutputPin, ScNode, TEXT("Input"));
								}
								SetReg(Inst.Args[0], { ScNode, TEXT(""), 4, D, false });
								continue;
							}
							bSawSceneDepthSample = true;
							FExprPin Bp; Bp.bIsBoilerplate = true;
							SetReg(Inst.Args[0], Bp);
							AddTrackedReg(SceneDepthRegs, Inst.Args[0]);
							continue; // Engine texture (SceneDepth / VolumetricFog), not a material texture
						}
					}
					else if (OrderedTextures.Num() > 0)
					{
						int32 RawTexIdx = TexBase.StartsWith(TEXT("t")) ? FCString::Atoi(*TexBase.Mid(1)) : 0;
						int32 TexIdx = FMath::Max(0, RawTexIdx - FirstMaterialTexSlot);
						const auto& TP = OrderedTextures[TexIdx % OrderedTextures.Num()];
						ResolvedTexName = TP.Key;
						ResolvedTex = TP.Value;
					}

					const int32 D = FMath::Max(1, UV.Depth - 1);
					UMaterialExpressionTextureSampleParameter2D* TexNode = Spawn<UMaterialExpressionTextureSampleParameter2D>(D);
					if (TexNode)
					{
						if (ResolvedTex)
						{
							const FString& FinalParamName = !ResolvedParamName.IsEmpty() ? ResolvedParamName : ResolvedTexName;
							TexNode->ParameterName = FName(*FinalParamName);
							TexNode->Texture = ResolvedTex;
							TexNode->AutoSetSampleType();
						}
						if (UV.IsValid())
						{
							FExprPin SafeUV = UV.NumChannels > 2 ? ApplyMask(UV, TEXT("xy"), true) : UV;
							ConnectSmart(SafeUV.Expr, SafeUV.OutputPin, TexNode, TEXT("UVs"));
						}
					}
					FString DstBase, DstMask;
					SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
					OutPin = ExtractTexSamplePin(TexNode, DstMask, TexSwiz, D);
					if (TexNode && TexNode->SamplerType == SAMPLERTYPE_Normal && OutPin.IsValid())
					{
						// SAMPLERTYPE_Normal already unpacks [0,1] -> [-1,1], whereas DXBC sample_b returns [0,1] before mad *2 - 1.
						const int32 Dn = FMath::Max(0, D - 1);
						UMaterialExpressionMultiply* Half = Spawn<UMaterialExpressionMultiply>(Dn);
						if (Half) Half->ConstB = 0.5f;
						ConnectSmart(OutPin.Expr, OutPin.OutputPin, Half, TEXT("A"));
						UMaterialExpressionAdd* Bias = Spawn<UMaterialExpressionAdd>(Dn);
						if (Bias) Bias->ConstB = 0.5f;
						ConnectSmart(Half, TEXT(""), Bias, TEXT("A"));
						OutPin = { Bias, TEXT(""), OutPin.NumChannels, Dn, false };
					}
				}

				if (Inst.bSaturate && OutPin.IsValid())
				{
					const int32 D = FMath::Max(0, OutPin.Depth - 1);
					UMaterialExpressionSaturate* Sat = Spawn<UMaterialExpressionSaturate>(D);
					ConnectSmart(OutPin.Expr, OutPin.OutputPin, Sat, TEXT(""));
					OutPin = { Sat, TEXT(""), OutPin.NumChannels, D, false };
				}

				if (OutPin.IsValid() || OutPin.bIsBoilerplate)
				{
					SetReg(Inst.Args[0], OutPin);
				}
			}

			// Connect final outputs to UE5 Material Root
			CurrentOpCode.Empty();
			CurrentDstMask.Empty();
			const bool bIsUnlit = Material->GetShadingModels().HasShadingModel(MSM_Unlit);
			const bool bIsAdditiveOrUnlit =
				Material->BlendMode == BLEND_Additive ||
				Material->BlendMode == BLEND_Translucent ||
				bIsUnlit;

			if (bIsDeferredGBuffer && !bIsAdditiveOrUnlit)
			{
				// UE Deferred GBuffer layout: o0=Emissive, o1=WorldNormal, o2=(Metallic,Specular,Roughness), o3=BaseColor
				FExprPin* O3 = RegValues.Find(TEXT("o3.xyz"));
				if (!O3) O3 = RegValues.Find(TEXT("o3"));
				if (O3 && O3->IsValid())
					ConnectToRootSmart(O3->Expr, O3->OutputPin, MP_BaseColor);

				FExprPin* O0 = RegValues.Find(TEXT("o0.xyz"));
				if (!O0) O0 = RegValues.Find(TEXT("o0"));
				if (O0 && O0->IsValid())
					ConnectToRootSmart(O0->Expr, O0->OutputPin, MP_EmissiveColor);

				FExprPin O2X, O2Y, O2Z;
				if (ResolveReg(TEXT("o2"), TEXT("x"), O2X) && O2X.IsValid())
					ConnectToRootSmart(O2X.Expr, O2X.OutputPin, MP_Metallic);
				if (ResolveReg(TEXT("o2"), TEXT("y"), O2Y) && O2Y.IsValid())
					ConnectToRootSmart(O2Y.Expr, O2Y.OutputPin, MP_Specular);
				if (ResolveReg(TEXT("o2"), TEXT("z"), O2Z) && O2Z.IsValid())
					ConnectToRootSmart(O2Z.Expr, O2Z.OutputPin, MP_Roughness);
				else if (O2Z.bIsBoilerplate)
					ConnectToRootSmart(MakeConst(0.0f, 1).Expr, TEXT(""), MP_Roughness);
				if (CapturedNormalPin.IsValid())
					ConnectToRootSmart(CapturedNormalPin.Expr, CapturedNormalPin.OutputPin, MP_Normal);
			}
			else
			{
				FExprPin* O0 = RegValues.Find(TEXT("o0.xyz"));
				if (!O0) O0 = RegValues.Find(TEXT("o0"));
				if (O0 && O0->IsValid())
				{
					ConnectToRootSmart(O0->Expr, O0->OutputPin, bIsAdditiveOrUnlit ? MP_EmissiveColor : MP_BaseColor);
					if (bIsUnlit)
						ConnectToRootSmart(O0->Expr, O0->OutputPin, MP_BaseColor);
				}
			}

			if (Material->BlendMode == BLEND_Additive && !CapturedOpacityPin.IsValid() && CapturedOpacityMaskPin.IsValid())
			{
				CapturedOpacityPin = CapturedOpacityMaskPin;
				CapturedOpacityMaskPin = FExprPin();
			}
			if (CapturedOpacityPin.IsValid())
				ConnectToRootSmart(CapturedOpacityPin.Expr, CapturedOpacityPin.OutputPin, MP_Opacity);
			if (CapturedOpacityMaskPin.IsValid())
				ConnectToRootSmart(CapturedOpacityMaskPin.Expr, CapturedOpacityMaskPin.OutputPin, MP_OpacityMask);
		}

		// Translate Vertex Shader DXBC WPO (GetMaterialWorldPositionOffset) -> MP_WorldPositionOffset
		bool TranslateVertexWpo(const FString& VertexShaderAsm)
		{
			if (VertexShaderAsm.IsEmpty()) return false;

			// 1. Parse VS header lines for View/Material CB slots, structured resource register, and clip-space SV_POSITION output
			TArray<FString> Lines;
			VertexShaderAsm.ParseIntoArrayLines(Lines, false);

			FString StructuredTexReg = TEXT("t0");
			FString PosOutputReg = TEXT("o5");
			int32 ParsedVsViewCb = -1;
			int32 MaxVsCb = 2;
			int32 ScannedViewCb = -1;

			for (const FString& RawHdr : Lines)
			{
				FString H = RawHdr.TrimStartAndEnd();
				if (H.StartsWith(TEXT("ue uniformBuffer View cb=")))
				{
					ParsedVsViewCb = FCString::Atoi(*H.Mid(25));
				}
				else if (H.StartsWith(TEXT("dcl_constantbuffer CB")))
				{
					int32 Idx = FCString::Atoi(*H.Mid(21));
					if (Idx > MaxVsCb) MaxVsCb = Idx;
				}
				else if (H.StartsWith(TEXT("dcl_resource_structured ")))
				{
					FString Rest = H.Mid(24).TrimStartAndEnd();
					int32 CommaIdx = INDEX_NONE;
					StructuredTexReg = Rest.FindChar(TEXT(','), CommaIdx) ? Rest.Left(CommaIdx).TrimStartAndEnd() : Rest;
				}
				else if (H.StartsWith(TEXT("dcl_output_siv ")))
				{
					FString Rest = H.Mid(15).TrimStartAndEnd();
					int32 DotIdx = INDEX_NONE, CommaIdx = INDEX_NONE;
					if (Rest.FindChar(TEXT('.'), DotIdx))
						PosOutputReg = Rest.Left(DotIdx).TrimStartAndEnd();
					else if (Rest.FindChar(TEXT(','), CommaIdx))
						PosOutputReg = Rest.Left(CommaIdx).TrimStartAndEnd();
				}
				else if (ScannedViewCb < 0)
				{
					int32 CbPos = H.Find(TEXT("cb"), ESearchCase::IgnoreCase);
					while (CbPos != INDEX_NONE)
					{
						int32 LBr = H.Find(TEXT("["), ESearchCase::CaseSensitive, ESearchDir::FromStart, CbPos);
						int32 RBr = (LBr != INDEX_NONE) ? H.Find(TEXT("]"), ESearchCase::CaseSensitive, ESearchDir::FromStart, LBr) : INDEX_NONE;
						if (LBr == CbPos + 3 || LBr == CbPos + 4)
						{
							FString CbIdxStr = H.Mid(CbPos + 2, LBr - (CbPos + 2));
							FString SlotStr = (RBr > LBr) ? H.Mid(LBr + 1, RBr - LBr - 1) : TEXT("");
							if (CbIdxStr.IsNumeric() && (SlotStr.Equals(TEXT("70")) || SlotStr.Equals(TEXT("142"))))
							{
								ScannedViewCb = FCString::Atoi(*CbIdxStr);
								break;
							}
						}
						CbPos = H.Find(TEXT("cb"), ESearchCase::IgnoreCase, ESearchDir::FromStart, CbPos + 2);
					}
				}
			}

			const FString SavedMatCbPrefix = MaterialCbPrefix;
			const FString SavedViewCbPrefix = ViewCbPrefix;
			const FString SavedMpcCbPrefix = MpcCbPrefix;
			const int32 SavedGraphYOffset = GraphYOffset;
			const TMap<int32, int32> SavedDepthRowCounters = DepthRowCounters;
			const TSet<FString> SavedPowCondRegs = PowCondRegs;
			const TMap<FString, FExprPin> SavedPowLogBases = PowLogBases;
			const TMap<FString, FCompareCond> SavedCompareConds = CompareConds;
			const bool SavedInVertexShader = bInVertexShader;
			const TMap<FString, EInputSemantic> SavedInputSemantics = InputSemantics;
			const TMap<FString, FExprPin> SavedParamCache = ParamCache;
			UMaterialExpressionWorldPosition* SavedWorldPos = CachedWorldPos;
			UMaterialExpressionTime* SavedTime = CachedTime;

			if (VsViewCbSlot >= 0)
				ViewCbPrefix = FString::Printf(TEXT("cb%d"), VsViewCbSlot);
			else if (ParsedVsViewCb >= 0)
				ViewCbPrefix = FString::Printf(TEXT("cb%d"), ParsedVsViewCb);
			else if (ScannedViewCb >= 0)
				ViewCbPrefix = FString::Printf(TEXT("cb%d"), ScannedViewCb);
			else
				ViewCbPrefix = TEXT("cb0");

			if (VsMaterialCbSlot >= 0)
				MaterialCbPrefix = FString::Printf(TEXT("cb%d"), VsMaterialCbSlot);
			else
				MaterialCbPrefix = FString::Printf(TEXT("cb%d"), MaxVsCb);

			MpcCbPrefix = (VsMpcCbSlot >= 0) ? FString::Printf(TEXT("cb%d"), VsMpcCbSlot) : TEXT("");

			auto RestoreState = [&]()
			{
				MaterialCbPrefix = SavedMatCbPrefix;
				ViewCbPrefix = SavedViewCbPrefix;
				MpcCbPrefix = SavedMpcCbPrefix;
				GraphYOffset = SavedGraphYOffset;
				DepthRowCounters = SavedDepthRowCounters;
				PowCondRegs = SavedPowCondRegs;
				PowLogBases = SavedPowLogBases;
				CompareConds = SavedCompareConds;
				bInVertexShader = SavedInVertexShader;
				InputSemantics = SavedInputSemantics;
				ParamCache = SavedParamCache;
				CachedWorldPos = SavedWorldPos;
				CachedTime = SavedTime;
			};

			// Bail out immediately if VS doesn't touch Material CB, Time, CameraPositionWS (SplineThicken), Trig, or Texture
			if (!VertexShaderAsm.Contains(MaterialCbPrefix + TEXT("[")) &&
				!VertexShaderAsm.Contains(ViewCbPrefix + TEXT("[142]")) &&
				!VertexShaderAsm.Contains(ViewCbPrefix + TEXT("[67]")) &&
				!VertexShaderAsm.Contains(TEXT("sincos")) &&
				!VertexShaderAsm.Contains(TEXT("sample_l")))
			{
				UE_LOG(LogUExplorerShaderTranslator, Log,
					TEXT("TranslateVertexWpo bailed out for '%s' (no %s[, %s[142], sincos, or sample_l in VS)"),
					*Material->GetName(), *MaterialCbPrefix, *ViewCbPrefix);
				RestoreState();
				return false;
			}

			GraphYOffset = 900;
			DepthRowCounters.Empty();
			RegValues.Empty();
			PowCondRegs.Empty();
			PowLogBases.Empty();
			CompareConds.Empty();
			PrimStructRowOffsets.Empty();
			InputSemantics.Empty();
			ParamCache.Empty();
			CachedWorldPos = nullptr;
			CachedTime = nullptr;
			bInVertexShader = true;

			const int32 StartNodeCount = NodeCounter;
			FString PreViewAddDst;
			FString VsTranslatedWorldPosReg;
			FString VsTranslatedWorldPosMask;
			FExprPin CapturedWpoPin;
			bool bUsedSplineThicken = false;
			bool bInWpoRegion = false;
			int32 VsTexCoordLoadIdx = 0;

			auto TrackVsPrimStructOffsets = [&](const FDxbcInstruction& InInst) -> bool
			{
				if (InInst.OpCode == TEXT("imul") && InInst.Args.Num() >= 4 && InInst.Args[0].Equals(TEXT("null"), ESearchCase::IgnoreCase))
				{
					FString DstBase, DstMask;
					SplitRegSwizzle(InInst.Args[1], DstBase, DstMask);
					if (DstMask.IsEmpty())
						PrimStructRowOffsets.Add(DstBase, 0);
					else
						for (int32 k = 0; k < DstMask.Len(); ++k)
							PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), 0);
					return true;
				}
				if (InInst.OpCode == TEXT("imad") && InInst.Args.Num() >= 4)
				{
					FString DstBase, DstMask;
					SplitRegSwizzle(InInst.Args[0], DstBase, DstMask);
					FString Lit = InInst.Args[3].TrimStartAndEnd();
					if (Lit.StartsWith(TEXT("l(")) && Lit.EndsWith(TEXT(")")))
					{
						FString Inner = Lit.Mid(2, Lit.Len() - 3);
						TArray<FString> Parts;
						Inner.ParseIntoArray(Parts, TEXT(","), true);
						TArray<int32> OffVals;
						for (const FString& P : Parts) OffVals.Add(FCString::Atoi(*P.TrimStartAndEnd()));
						if (OffVals.Num() == 4 && !DstMask.IsEmpty() && !DstMask.Equals(TEXT("xyzw")))
						{
							for (int32 k = 0; k < DstMask.Len(); ++k)
							{
								const int32 LaneIdx = SwizzleCharToIndex(DstMask[k]);
								PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), OffVals.IsValidIndex(LaneIdx) ? OffVals[LaneIdx] : 0);
							}
						}
						else if (OffVals.Num() >= 1)
						{
							if (DstMask.IsEmpty())
								PrimStructRowOffsets.Add(DstBase, OffVals[0]);
							else
								for (int32 k = 0; k < DstMask.Len(); ++k)
									PrimStructRowOffsets.Add(FString::Printf(TEXT("%s.%c"), *DstBase, DstMask[k]), OffVals[FMath::Min(k, OffVals.Num() - 1)]);
						}
					}
					return true;
				}
				return false;
			};

			auto HandleVsStructuredLoad = [&](const FDxbcInstruction& InInst) -> bool
			{
				if (InInst.OpCode != TEXT("ld_structured_indexable") || InInst.Args.Num() < 4)
					return false;
				FString TexBase, TexSwiz;
				SplitRegSwizzle(InInst.Args[3], TexBase, TexSwiz);
				if (!TexBase.Equals(StructuredTexReg, ESearchCase::IgnoreCase))
					return true;

				FString IdxBase, IdxSwiz;
				SplitRegSwizzle(InInst.Args[1], IdxBase, IdxSwiz);
				FString IdxEff = ComputeEffectiveSwizzle(IdxSwiz, true);
				int32 RowOff = -1;
				if (const int32* ExactOff = PrimStructRowOffsets.Find(InInst.Args[1])) RowOff = *ExactOff;
				else if (!IdxEff.IsEmpty())
				{
					if (const int32* LaneOff = PrimStructRowOffsets.Find(FString::Printf(TEXT("%s.%c"), *IdxBase, IdxEff[0])))
						RowOff = *LaneOff;
				}
				if (RowOff < 0)
				{
					if (const int32* BaseOff = PrimStructRowOffsets.Find(IdxBase)) RowOff = *BaseOff;
				}

				if ((RowOff >= 0 && RowOff <= 2) || (RowOff >= 6 && RowOff <= 8))
				{
					const bool bWorldToLocal = (RowOff >= 6);
					const int32 Axis = bWorldToLocal ? (RowOff - 6) : RowOff;
					float ByteOffVal = 0.0f;
					const int32 CompIdx = TryParseSingleLiteral(InInst.Args[2], ByteOffVal) ? FMath::Clamp(static_cast<int32>(ByteOffVal) / 4, 0, 3) : 0;
					UMaterialExpressionConstant3Vector* Basis = Spawn<UMaterialExpressionConstant3Vector>(9);
					if (Basis)
						Basis->Constant = (Axis == 0) ? FLinearColor(1, 0, 0, 1) : (Axis == 1) ? FLinearColor(0, 1, 0, 1) : FLinearColor(0, 0, 1, 1);
					UMaterialExpressionTransform* Xf = Spawn<UMaterialExpressionTransform>(8);
					if (Xf)
					{
						Xf->TransformSourceType = bWorldToLocal ? TRANSFORMSOURCE_World : TRANSFORMSOURCE_Local;
						Xf->TransformType = bWorldToLocal ? TRANSFORM_Local : TRANSFORM_World;
						ConnectSmart(Basis, TEXT(""), Xf, TEXT(""));
					}
					FExprPin BasisPin = { Xf ? static_cast<UMaterialExpression*>(Xf) : static_cast<UMaterialExpression*>(Basis), TEXT(""), 3, 8, false };
					if (CurrentDstMask.Len() == 1 && CompIdx > 0)
					{
						const TCHAR CompChars[4] = { TEXT('x'), TEXT('y'), TEXT('z'), TEXT('w') };
						SetReg(InInst.Args[0], ApplyMask(BasisPin, FString::Chr(CompChars[CompIdx]), true));
					}
					else
					{
						if (!CurrentDstMask.IsEmpty() && CurrentDstMask.Len() < 3) BasisPin = ApplyMask(BasisPin, CurrentDstMask, true);
						SetReg(InInst.Args[0], BasisPin);
					}
				}
				else if (RowOff == 3 || RowOff == 5 || RowOff == 18)
				{
					UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(8);
					SetReg(InInst.Args[0], MakeDemotedWS(ObjPos, 7));
				}
				else if (RowOff == 9)
				{
					UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(9);
					FExprPin PosPin = MakeDemotedWS(ObjPos, 8);
					UMaterialExpressionMultiply* NegPos = Spawn<UMaterialExpressionMultiply>(8);
					if (NegPos) { NegPos->ConstB = -1.0f; ConnectSmart(PosPin.Expr, PosPin.OutputPin, NegPos, TEXT("A")); }
					UMaterialExpressionTransform* Xf = Spawn<UMaterialExpressionTransform>(7);
					if (Xf)
					{
						Xf->TransformSourceType = TRANSFORMSOURCE_World;
						Xf->TransformType = TRANSFORM_Local;
						ConnectSmart(NegPos, TEXT(""), Xf, TEXT(""));
					}
					FExprPin Out9 = { Xf, TEXT(""), 3, 7, false };
					if (!CurrentDstMask.IsEmpty() && CurrentDstMask.Len() < 3) Out9 = ApplyMask(Out9, CurrentDstMask, true);
					SetReg(InInst.Args[0], Out9);
				}
				else if (RowOff == 4)
				{
					UMaterialExpressionConstant3Vector* One3 = Spawn<UMaterialExpressionConstant3Vector>(9);
					if (One3) One3->Constant = FLinearColor(1, 1, 1, 1);
					UMaterialExpressionTransform* Xf = Spawn<UMaterialExpressionTransform>(8);
					if (Xf)
					{
						Xf->TransformSourceType = TRANSFORMSOURCE_World;
						Xf->TransformType = TRANSFORM_Local;
						ConnectSmart(One3, TEXT(""), Xf, TEXT(""));
					}
					FExprPin InvScalePin = { Xf, TEXT(""), 3, 8, false };
					SetReg(InInst.Args[0], CombinePins(InvScalePin, MakeConst(1.0f, 8)));
				}
				else
				{
					// Offsets >= 19 (e.g. 32, 33, 35 = Fortnite CustomPrimitiveData pickaxe-hit bounce) -> mark as boilerplate
					FExprPin Bp; Bp.bIsBoilerplate = true;
					SetReg(InInst.Args[0], Bp);
				}
				return true;
			};

			for (const FString& RawLine : Lines)
			{
				FString Line = RawLine.TrimStartAndEnd();
				if (Line.IsEmpty() || Line.StartsWith(TEXT("//")) || Line.StartsWith(TEXT("dcl_")) ||
					Line.StartsWith(TEXT("vs_")) || Line.StartsWith(TEXT("[")) || Line.StartsWith(TEXT("ue ")) ||
					Line.StartsWith(TEXT("status=")) || Line.StartsWith(TEXT("target=")) || Line.StartsWith(TEXT("(")))
				{
					continue;
				}

				FDxbcInstruction Inst;
				if (!ParseInstruction(Line, Inst)) continue;
				SetActiveInst(Inst);

				if (Inst.OpCode == TEXT("ret"))
					break;

				// Stop once we reach clip-space projection inside WPO region (mul ..., <ViewCb>[1] or add <PosOutputReg>, ...)
				if (bInWpoRegion)
				{
					if (Inst.OpCode == TEXT("mul") && Inst.Args.Num() >= 3 &&
						(Inst.Args[1].Contains(ViewCbPrefix + TEXT("[1]")) || Inst.Args[2].Contains(ViewCbPrefix + TEXT("[1]"))))
					{
						break;
					}
					if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3)
					{
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						if (DstBase.Equals(PosOutputReg, ESearchCase::IgnoreCase))
							break;
					}
					if (Inst.OpCode == TEXT("if_nz") || Inst.OpCode == TEXT("if_z"))
						break;
				}

				// Phase 1: Detect BasePassVS boilerplate establishing LocalToWorld basis, ObjectWorldPosition, and TranslatedWorldPosition
				if (!bInWpoRegion)
				{
					if (TrackVsPrimStructOffsets(Inst) || HandleVsStructuredLoad(Inst))
						continue;
					if (Inst.OpCode == TEXT("ld_indexable") && Inst.Args.Num() >= 3 && CurrentDstMask.Equals(TEXT("xyzw")))
					{
						// Vertex color buffer fetch in Phase 1 (e.g. LocalVF t4)
						if (!CachedVertexColor) CachedVertexColor = Spawn<UMaterialExpressionVertexColor>(8);
						FExprPin VcRgb = { CachedVertexColor, TEXT(""), 3, 8, false };
						FExprPin VcA = { CachedVertexColor, TEXT("A"), 1, 8, false };
						SetReg(Inst.Args[0], CombinePins(VcRgb, VcA));
						continue;
					}
					if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4 && CurrentDstMask.Equals(TEXT("yzw")) && Inst.Args[0].StartsWith(TEXT("r0.")))
					{
						// Phase 1 tangent-to-world basis in r0.yzw: r6.yyzx is BinormalWS (N x T), otherwise VertexNormalWS
						if (!CachedVertexNormalWS) CachedVertexNormalWS = Spawn<UMaterialExpressionVertexNormalWS>(8);
						if (Inst.Args[2].Contains(TEXT(".yyzx")))
						{
							bUsedSplineThicken = true;
							UMaterialExpressionVertexTangentWS* VT = Spawn<UMaterialExpressionVertexTangentWS>(8);
							FExprPin NPin = { CachedVertexNormalWS, TEXT(""), 3, 8, false };
							FExprPin TPin = { VT, TEXT(""), 3, 8, false };
							FExprPin NYzx = ApplyMask(NPin, TEXT("yzx"), true);
							FExprPin TZxy = ApplyMask(TPin, TEXT("zxy"), true);
							FExprPin NZxy = ApplyMask(NPin, TEXT("zxy"), true);
							FExprPin TYzx = ApplyMask(TPin, TEXT("yzx"), true);
							UMaterialExpressionMultiply* M1 = Spawn<UMaterialExpressionMultiply>(7);
							ConnectSmart(NYzx.Expr, NYzx.OutputPin, M1, TEXT("A"));
							ConnectSmart(TZxy.Expr, TZxy.OutputPin, M1, TEXT("B"));
							UMaterialExpressionMultiply* M2 = Spawn<UMaterialExpressionMultiply>(7);
							ConnectSmart(NZxy.Expr, NZxy.OutputPin, M2, TEXT("A"));
							ConnectSmart(TYzx.Expr, TYzx.OutputPin, M2, TEXT("B"));
							UMaterialExpressionMultiply* NegM2 = Spawn<UMaterialExpressionMultiply>(6);
							if (NegM2) NegM2->ConstB = -1.0f;
							ConnectSmart(M2, TEXT(""), NegM2, TEXT("A"));
							UMaterialExpressionAdd* CrossNode = Spawn<UMaterialExpressionAdd>(5);
							ConnectSmart(M1, TEXT(""), CrossNode, TEXT("A"));
							ConnectSmart(NegM2, TEXT(""), CrossNode, TEXT("B"));
							SetReg(Inst.Args[0], { CrossNode, TEXT(""), 3, 5, false });
						}
						else
						{
							SetReg(Inst.Args[0], { CachedVertexNormalWS, TEXT(""), 3, 8, false });
						}
						continue;
					}
					if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3)
					{
						if (Inst.Args[2].Contains(ViewCbPrefix + TEXT("[70]")) && !Inst.Args[2].StartsWith(TEXT("-")))
						{
							FString DstBase, DstMask;
							SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
							PreViewAddDst = DstBase;

							// Bind ObjectWorldPosition register (Args[1]) on the exact lanes read by this add
							FString ObjBase, ObjSwiz;
							SplitRegSwizzle(Inst.Args[1], ObjBase, ObjSwiz);
							FString ObjEff = ComputeEffectiveSwizzle(ObjSwiz, false);
							UMaterialExpressionObjectPositionWS* ObjPos = Spawn<UMaterialExpressionObjectPositionWS>(8);
							FExprPin ObjPin = MakeDemotedWS(ObjPos, 7);
							if (ObjEff.Len() == 3)
								SetReg(ObjBase + TEXT(".") + ObjEff, ObjPin);
							else
								SetReg(ObjBase + TEXT(".xyz"), ObjPin);
							continue;
						}
						if (!PreViewAddDst.IsEmpty())
						{
							FString Base1, Sw1, Base2, Sw2;
							SplitRegSwizzle(Inst.Args[1], Base1, Sw1);
							SplitRegSwizzle(Inst.Args[2], Base2, Sw2);
							if (Base1.Equals(PreViewAddDst) || Base2.Equals(PreViewAddDst))
							{
								FString DstBase, DstMask;
								SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
								VsTranslatedWorldPosReg = DstBase;
								VsTranslatedWorldPosMask = DstMask.IsEmpty() ? TEXT("xyz") : DstMask;
								bInWpoRegion = true;
								continue;
							}
						}
					}
					continue;
				}

				// Phase 2: Translate WPO math between TranslatedWorldPosition and clip-space projection
				if (TrackVsPrimStructOffsets(Inst) || HandleVsStructuredLoad(Inst))
					continue;

				if (Inst.OpCode == TEXT("ld_indexable") && Inst.Args.Num() >= 3)
				{
					UMaterialExpressionTextureCoordinate* UV = Spawn<UMaterialExpressionTextureCoordinate>(8);
					if (UV) UV->CoordinateIndex = FMath::Min(VsTexCoordLoadIdx++, 3);
					SetReg(Inst.Args[0], { UV, TEXT(""), 2, 8, false });
					continue;
				}

				// AbsoluteWorldPosition = TranslatedWorldPosition - PreViewTranslation (<ViewCb>[70])
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 &&
					Inst.Args[2].Contains(TEXT("-") + ViewCbPrefix + TEXT("[70]")))
				{
					FString Base1, Sw1;
					SplitRegSwizzle(Inst.Args[1], Base1, Sw1);
					FString Eff1 = ComputeEffectiveSwizzle(Sw1, false);
					UMaterialExpressionWorldPosition* WP = Spawn<UMaterialExpressionWorldPosition>(8);
					FExprPin WpPin = MakeDemotedWS(WP, 7);
					if (!Eff1.IsEmpty() && !Eff1.Equals(TEXT("xyz"))) WpPin = ApplyMask(WpPin, Eff1, true);
					SetReg(Inst.Args[0], WpPin);
					continue;
				}

				// Final WPO addition: add rFinal.xyz, rWpo, rTranslatedWP  OR  mad rFinal.xyz, rA, rB, rTranslatedWP
				if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4 && !VsTranslatedWorldPosReg.IsEmpty() && CurrentDstMask.Len() == 3)
				{
					FString Base3, Sw3;
					SplitRegSwizzle(Inst.Args[3], Base3, Sw3);
					if (Base3 == VsTranslatedWorldPosReg && !Inst.Args[3].StartsWith(TEXT("-")) &&
						(VsTranslatedWorldPosMask.IsEmpty() || ComputeEffectiveSwizzle(Sw3, false).Equals(VsTranslatedWorldPosMask)))
					{
						FExprPin A = ResolveOperand(Inst.Args[1]);
						FExprPin B = ResolveOperand(Inst.Args[2]);
						if (A.IsValid() && B.IsValid() && !A.bIsBoilerplate && !B.bIsBoilerplate)
						{
							MatchBinaryChannels(A, B);
							const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
							UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(D);
							ConnectSmart(A.Expr, A.OutputPin, MulNode, TEXT("A"));
							ConnectSmart(B.Expr, B.OutputPin, MulNode, TEXT("B"));
							CapturedWpoPin = { MulNode, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
							FString DstBase, DstMask;
							SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
							VsTranslatedWorldPosReg = DstBase;
							VsTranslatedWorldPosMask = DstMask.IsEmpty() ? TEXT("xyz") : DstMask;
							continue;
						}
					}
				}
				if (Inst.OpCode == TEXT("add") && Inst.Args.Num() >= 3 && !VsTranslatedWorldPosReg.IsEmpty() && CurrentDstMask.Len() == 3)
				{
					FString Base1, Sw1, Base2, Sw2;
					SplitRegSwizzle(Inst.Args[1], Base1, Sw1);
					SplitRegSwizzle(Inst.Args[2], Base2, Sw2);
					if (Base1 == VsTranslatedWorldPosReg && !Inst.Args[1].StartsWith(TEXT("-")) &&
						(VsTranslatedWorldPosMask.IsEmpty() || ComputeEffectiveSwizzle(Sw1, false).Equals(VsTranslatedWorldPosMask)))
					{
						FExprPin Candidate = ResolveOperand(Inst.Args[2]);
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						VsTranslatedWorldPosReg = DstBase;
						VsTranslatedWorldPosMask = DstMask.IsEmpty() ? TEXT("xyz") : DstMask;
						if (Candidate.IsValid() && !Candidate.bIsBoilerplate) { CapturedWpoPin = Candidate; }
						continue;
					}
					if (Base2 == VsTranslatedWorldPosReg && !Inst.Args[2].StartsWith(TEXT("-")) &&
						(VsTranslatedWorldPosMask.IsEmpty() || ComputeEffectiveSwizzle(Sw2, false).Equals(VsTranslatedWorldPosMask)))
					{
						FExprPin Candidate = ResolveOperand(Inst.Args[1]);
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						VsTranslatedWorldPosReg = DstBase;
						VsTranslatedWorldPosMask = DstMask.IsEmpty() ? TEXT("xyz") : DstMask;
						if (Candidate.IsValid() && !Candidate.bIsBoilerplate) { CapturedWpoPin = Candidate; }
						continue;
					}
				}

				// Compare instructions saved for movc
				if (Inst.OpCode == TEXT("ge") && Inst.Args.Num() >= 3)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					if (A.IsValid() && B.IsValid())
					{
						FCompareCond Cmp{ A, B, ECompareOp::Ge };
						CompareConds.Add(Inst.Args[0], Cmp);
					}
					continue;
				}
				if (Inst.OpCode == TEXT("lt") && Inst.Args.Num() >= 3)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					if (A.IsValid() && B.IsValid())
					{
						FCompareCond Cmp{ A, B, ECompareOp::Lt };
						CompareConds.Add(Inst.Args[0], Cmp);
					}
					continue;
				}

				// sincos dstSin, dstCos, srcRadians
				if (Inst.OpCode == TEXT("sincos") && Inst.Args.Num() >= 3)
				{
					FExprPin Angle = ResolveOperand(Inst.Args[2]);
					if (Angle.IsValid())
					{
						const int32 D = FMath::Max(0, Angle.Depth - 1);
						if (!Inst.Args[0].Equals(TEXT("null"), ESearchCase::IgnoreCase))
						{
							UMaterialExpressionSine* SinNode = Spawn<UMaterialExpressionSine>(D);
							if (SinNode) SinNode->Period = UE_TWO_PI;
							ConnectSmart(Angle.Expr, Angle.OutputPin, SinNode, TEXT(""));
							SetReg(Inst.Args[0], { SinNode, TEXT(""), Angle.NumChannels, D, false });
						}
						if (!Inst.Args[1].Equals(TEXT("null"), ESearchCase::IgnoreCase))
						{
							UMaterialExpressionCosine* CosNode = Spawn<UMaterialExpressionCosine>(D);
							if (CosNode) CosNode->Period = UE_TWO_PI;
							ConnectSmart(Angle.Expr, Angle.OutputPin, CosNode, TEXT(""));
							SetReg(Inst.Args[1], { CosNode, TEXT(""), Angle.NumChannels, D, false });
						}
					}
					continue;
				}

				FExprPin OutPin;
				if (Inst.OpCode == TEXT("movc") && Inst.Args.Num() >= 4)
				{
					FString CondBase, CondSwiz;
					SplitRegSwizzle(Inst.Args[1], CondBase, CondSwiz);
					FString CondEff = ComputeEffectiveSwizzle(CondSwiz, false);
					const FString CondEffKey = CondEff.IsEmpty() ? CondBase : (CondBase + TEXT(".") + CondEff);

					FCompareCond* CmpPtr = CompareConds.Find(Inst.Args[1]);
					if (!CmpPtr) CmpPtr = CompareConds.Find(CondEffKey);
					if (!CmpPtr) CmpPtr = CompareConds.Find(CondBase);
					FCompareCond CmpCopy;
					const bool bHasCmp = (CmpPtr != nullptr);
					if (bHasCmp) CmpCopy = *CmpPtr;

					FExprPin TrueVal = ResolveOperand(Inst.Args[2]);
					FExprPin FalseVal = ResolveOperand(Inst.Args[3]);
					if (bHasCmp && TrueVal.IsValid() && FalseVal.IsValid())
					{
						OutPin = EmitCompareIf(CmpCopy, TrueVal, FalseVal);
					}
					else if (TrueVal.IsValid())
					{
						OutPin = TrueVal;
					}
					else if (FalseVal.IsValid())
					{
						OutPin = FalseVal;
					}
				}
				else if (Inst.OpCode == TEXT("mov") && Inst.Args.Num() >= 2)
				{
					OutPin = ResolveOperand(Inst.Args[1]);
				}
				else if ((Inst.OpCode == TEXT("add") || Inst.OpCode == TEXT("mul") || Inst.OpCode == TEXT("div") ||
					Inst.OpCode == TEXT("min") || Inst.OpCode == TEXT("max") ||
					Inst.OpCode == TEXT("dp2") || Inst.OpCode == TEXT("dp3") || Inst.OpCode == TEXT("dp4")) && Inst.Args.Num() >= 3)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					const bool bIsDot = Inst.OpCode.StartsWith(TEXT("dp"));
					if (A.bIsBoilerplate || B.bIsBoilerplate)
					{
						OutPin.bIsBoilerplate = true;
					}
					else if (A.IsValid() && B.IsValid())
					{
						MatchBinaryChannels(A, B);
						const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
						const int32 OutCh = bIsDot ? 1 : FMath::Max(A.NumChannels, B.NumChannels);
						UMaterialExpression* Node = nullptr;
						if (Inst.OpCode == TEXT("add")) Node = Spawn<UMaterialExpressionAdd>(D);
						else if (Inst.OpCode == TEXT("mul")) Node = Spawn<UMaterialExpressionMultiply>(D);
						else if (Inst.OpCode == TEXT("div")) Node = Spawn<UMaterialExpressionDivide>(D);
						else if (Inst.OpCode == TEXT("min")) Node = Spawn<UMaterialExpressionMin>(D);
						else if (Inst.OpCode == TEXT("max")) Node = Spawn<UMaterialExpressionMax>(D);
						else Node = Spawn<UMaterialExpressionDotProduct>(D);
						if (Node)
						{
							ConnectSmart(A.Expr, A.OutputPin, Node, TEXT("A"));
							ConnectSmart(B.Expr, B.OutputPin, Node, TEXT("B"));
							OutPin = { Node, TEXT(""), OutCh, D, false };
						}
					}
				}
				else if (Inst.OpCode == TEXT("mad") && Inst.Args.Num() >= 4)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					FExprPin B = ResolveOperand(Inst.Args[2]);
					FExprPin C = ResolveOperand(Inst.Args[3]);
					if (A.bIsBoilerplate || B.bIsBoilerplate)
					{
						if (C.IsValid() && !C.bIsBoilerplate) OutPin = C;
						else OutPin.bIsBoilerplate = true;
					}
					else if (A.IsValid() && B.IsValid())
					{
						MatchBinaryChannels(A, B);
						const int32 D = FMath::Max(0, FMath::Min(A.Depth, B.Depth) - 1);
						UMaterialExpressionMultiply* MulNode = Spawn<UMaterialExpressionMultiply>(D);
						ConnectSmart(A.Expr, A.OutputPin, MulNode, TEXT("A"));
						ConnectSmart(B.Expr, B.OutputPin, MulNode, TEXT("B"));
						FExprPin Prod = { MulNode, TEXT(""), FMath::Max(A.NumChannels, B.NumChannels), D, false };
						if (C.IsValid() && !C.bIsBoilerplate)
						{
							MatchBinaryChannels(Prod, C);
							const int32 D2 = FMath::Max(0, FMath::Min(Prod.Depth, C.Depth) - 1);
							UMaterialExpressionAdd* AddNode = Spawn<UMaterialExpressionAdd>(D2);
							ConnectSmart(Prod.Expr, Prod.OutputPin, AddNode, TEXT("A"));
							ConnectSmart(C.Expr, C.OutputPin, AddNode, TEXT("B"));
							OutPin = { AddNode, TEXT(""), FMath::Max(Prod.NumChannels, C.NumChannels), D2, false };
						}
						else OutPin = Prod;
					}
				}
				else if ((Inst.OpCode == TEXT("frc") || Inst.OpCode == TEXT("sqrt") || Inst.OpCode == TEXT("rsq") ||
					Inst.OpCode == TEXT("exp") || Inst.OpCode == TEXT("round_ni") || Inst.OpCode == TEXT("round_pi")) && Inst.Args.Num() >= 2)
				{
					FExprPin A = ResolveOperand(Inst.Args[1]);
					if (A.IsValid())
					{
						const int32 D = FMath::Max(0, A.Depth - 1);
						if (Inst.OpCode == TEXT("frc"))
						{
							UMaterialExpressionFrac* F = Spawn<UMaterialExpressionFrac>(D);
							ConnectSmart(A.Expr, A.OutputPin, F, TEXT(""));
							OutPin = { F, TEXT(""), A.NumChannels, D, false };
						}
						else if (Inst.OpCode == TEXT("sqrt"))
						{
							UMaterialExpressionSquareRoot* S = Spawn<UMaterialExpressionSquareRoot>(D);
							ConnectSmart(A.Expr, A.OutputPin, S, TEXT(""));
							OutPin = { S, TEXT(""), A.NumChannels, D, false };
						}
						else if (Inst.OpCode == TEXT("rsq"))
						{
							UMaterialExpressionSquareRoot* S = Spawn<UMaterialExpressionSquareRoot>(D);
							ConnectSmart(A.Expr, A.OutputPin, S, TEXT(""));
							UMaterialExpressionDivide* Div = Spawn<UMaterialExpressionDivide>(FMath::Max(0, D - 1));
							if (Div) Div->ConstA = 1.0f;
							ConnectSmart(S, TEXT(""), Div, TEXT("B"));
							OutPin = { Div, TEXT(""), A.NumChannels, FMath::Max(0, D - 1), false };
						}
						else if (Inst.OpCode == TEXT("exp"))
						{
							FExprPin Two = MakeConst(2.0f, D + 1);
							UMaterialExpressionPower* Pow = Spawn<UMaterialExpressionPower>(D);
							ConnectSmart(Two.Expr, Two.OutputPin, Pow, TEXT("Base"));
							ConnectSmart(A.Expr, A.OutputPin, Pow, TEXT("Exp"));
							OutPin = { Pow, TEXT(""), A.NumChannels, D, false };
						}
						else if (Inst.OpCode == TEXT("round_ni"))
						{
							UMaterialExpressionFloor* Fl = Spawn<UMaterialExpressionFloor>(D);
							ConnectSmart(A.Expr, A.OutputPin, Fl, TEXT(""));
							OutPin = { Fl, TEXT(""), A.NumChannels, D, false };
						}
						else
						{
							UMaterialExpressionCeil* Ce = Spawn<UMaterialExpressionCeil>(D);
							ConnectSmart(A.Expr, A.OutputPin, Ce, TEXT(""));
							OutPin = { Ce, TEXT(""), A.NumChannels, D, false };
						}
					}
				}
				else if (Inst.OpCode.StartsWith(TEXT("sample")) && Inst.Args.Num() >= 3)
				{
					FExprPin UV = ResolveOperand(Inst.Args[1]);
					if (!UV.bIsBoilerplate)
					{
						FString TexReg = Inst.Args[2];
						FString TexBase, TexSwiz;
						SplitRegSwizzle(TexReg, TexBase, TexSwiz);

						UTexture2D* ResolvedTex = nullptr;
						FString ResolvedTexName;
						FString ResolvedParamName;
						if (VsTextureSlots.Num() > 0)
						{
							if (const FString* ExactName = VsTextureSlots.Find(TexBase))
							{
								ResolvedTexName = *ExactName;
								if (UTexture2D** Found = TextureByName.Find(*ExactName))
									ResolvedTex = *Found;
								if (const FString* ParamName = VsTextureParamNames.Find(TexBase); ParamName && !ParamName->IsEmpty())
									ResolvedParamName = *ParamName;
							}
							else
							{
								continue; // Engine texture, not a material texture
							}
						}
						else if (OrderedTextures.Num() > 0)
						{
							int32 RawTexIdx = TexBase.StartsWith(TEXT("t")) ? FCString::Atoi(*TexBase.Mid(1)) : 0;
							const auto& TP = OrderedTextures[FMath::Max(0, RawTexIdx) % OrderedTextures.Num()];
							ResolvedTexName = TP.Key;
							ResolvedTex = TP.Value;
						}

						const int32 D = FMath::Max(1, UV.IsValid() ? (UV.Depth - 1) : 6);
						UMaterialExpressionTextureSampleParameter2D* TexNode = Spawn<UMaterialExpressionTextureSampleParameter2D>(D);
						if (TexNode)
						{
							if (ResolvedTex)
							{
								const FString& FinalParamName = !ResolvedParamName.IsEmpty() ? ResolvedParamName : ResolvedTexName;
								TexNode->ParameterName = FName(*FinalParamName);
								TexNode->Texture = ResolvedTex;
								TexNode->AutoSetSampleType();
							}
							if (UV.IsValid())
							{
								FExprPin SafeUV = UV.NumChannels > 2 ? ApplyMask(UV, TEXT("xy"), true) : UV;
								ConnectSmart(SafeUV.Expr, SafeUV.OutputPin, TexNode, TEXT("UVs"));
							}
						}
						FString DstBase, DstMask;
						SplitRegSwizzle(Inst.Args[0], DstBase, DstMask);
						OutPin = ExtractTexSamplePin(TexNode, DstMask, TexSwiz, D);
					}
				}

				if (Inst.bSaturate && OutPin.IsValid())
				{
					const int32 D = FMath::Max(0, OutPin.Depth - 1);
					UMaterialExpressionSaturate* Sat = Spawn<UMaterialExpressionSaturate>(D);
					ConnectSmart(OutPin.Expr, OutPin.OutputPin, Sat, TEXT(""));
					OutPin = { Sat, TEXT(""), OutPin.NumChannels, D, false };
				}

				if (OutPin.IsValid() || OutPin.bIsBoilerplate)
				{
					SetReg(Inst.Args[0], OutPin);
				}
			}

			FExprPin WpoOut = CapturedWpoPin;
			const int32 WpoNodesSpawned = NodeCounter - StartNodeCount;
			const bool bUsedFallback = !CapturedWpoPin.IsValid() && WpoOut.IsValid();
			UE_LOG(LogUExplorerShaderTranslator, Log,
				TEXT("Translated VS WPO for '%s': %d nodes (InWpoRegion=%d, TWP=%s.%s, Captured=%d, Fallback=%d, View=%s, Mat=%s, MPC=%s, Struct=%s, Pos=%s)"),
				*Material->GetName(), WpoNodesSpawned, bInWpoRegion ? 1 : 0,
				*VsTranslatedWorldPosReg, *VsTranslatedWorldPosMask,
				CapturedWpoPin.IsValid() ? 1 : 0, bUsedFallback ? 1 : 0,
				*ViewCbPrefix, *MaterialCbPrefix, *MpcCbPrefix, *StructuredTexReg, *PosOutputReg);

			if (WpoOut.IsValid())
			{
#if !defined(UEXPLORER_HEADLESS)
				if (bUsedSplineThicken && VertexShaderAsm.Contains(ViewCbPrefix + TEXT("[67]")))
				{
					if (UMaterialFunction* SplineThickenMF = LoadObject<UMaterialFunction>(nullptr, TEXT("/Engine/Functions/Engine_MaterialFunctions01/WorldPositionOffset/SplineThicken.SplineThicken")))
					{
						UMaterialExpressionMaterialFunctionCall* MFCall = Spawn<UMaterialExpressionMaterialFunctionCall>(2);
						if (MFCall && MFCall->SetMaterialFunction(SplineThickenMF))
						{
							MFCall->UpdateFromFunctionResource(true);
							for (const FFunctionExpressionInput& In : MFCall->FunctionInputs)
							{
								const FString InName = In.ExpressionInput ? In.ExpressionInput->InputName.ToString() : TEXT("");
								if (InName.Contains(TEXT("Width")))
								{
									ConnectSmart(MakeConst(1.0f, 3).Expr, TEXT(""), MFCall, InName);
								}
								else if (InName.Contains(TEXT("UV")))
								{
									ConnectSmart(GetOrSpawnTexCoord(0).Expr, TEXT(""), MFCall, InName);
								}
							}
							FString OutName;
							for (const FFunctionExpressionOutput& Out : MFCall->FunctionOutputs)
							{
								if (Out.ExpressionOutput)
								{
									const FString Cand = Out.ExpressionOutput->OutputName.ToString();
									if (Cand.Contains(TEXT("World")) || OutName.IsEmpty())
										OutName = Cand;
								}
							}
							WpoOut = { MFCall, OutName, 3, 2, false };
						}
					}
				}
#endif
				if (WpoOut.NumChannels > 3) WpoOut = ApplyMask(WpoOut, TEXT("xyz"), true);
				ConnectToRootSmart(WpoOut.Expr, WpoOut.OutputPin, MP_WorldPositionOffset);
				RestoreState();
				return true;
			}
			RestoreState();
			return false;
		}
	};
}

bool FUExplorerShaderTranslator::TranslateMaterialShader(
	UMaterial* TargetMaterial,
	const FString& PixelShaderAsm,
	const TSharedPtr<FJsonObject>& MatObj,
	const TMap<FString, UTexture2D*>& ImportedTextures)
{
	if (!TargetMaterial || PixelShaderAsm.IsEmpty())
	{
		return false;
	}

	FDxbcTranslatorContext Ctx(TargetMaterial, MatObj, ImportedTextures);
	Ctx.ParseAndBuild(PixelShaderAsm);
	const bool bHasO0 = Ctx.RegValues.Contains(TEXT("o0")) || Ctx.RegValues.Contains(TEXT("o0.xyz"));

	bool bConnectedWpo = false;
	FString VertexShaderAsm;
	if (MatObj.IsValid() && MatObj->TryGetStringField(TEXT("VertexShaderAsm"), VertexShaderAsm) && !VertexShaderAsm.IsEmpty())
	{
		bConnectedWpo = Ctx.TranslateVertexWpo(VertexShaderAsm);
	}
	else
	{
		UE_LOG(LogUExplorerShaderTranslator, Log, TEXT("TranslateMaterialShader '%s': VertexShaderAsm empty (no VS translated)"), *TargetMaterial->GetName());
	}

	UE_LOG(LogUExplorerShaderTranslator, Log,
		TEXT("Translated DXBC Shaders for '%s' -> %d UE5 nodes (o0=%d, Opacity=%d, WPO=%d)"),
		*TargetMaterial->GetName(), Ctx.NodeCounter,
		bHasO0 ? 1 : 0,
		Ctx.CapturedOpacityPin.IsValid() ? 1 : 0,
		bConnectedWpo ? 1 : 0);

	return Ctx.NodeCounter > 0;
}

void FUExplorerShaderTranslator::RunElimDeviceTest()
{
	UE_LOG(LogUExplorerShaderTranslator, Log, TEXT("=== Running ElimDevice 1:1 Shader Translator Test ==="));

	// Prefer the FNiagaraMeshVertexFactory PS because FLocalVertexFactory constant-folds ParticleColor and DynamicParameter away!
	FString DumpAsmPath = TEXT("C:/Users/editza/Documents/FUnreal/FModel+UnrealRenderer/UExplorer/bin/Debug/net10.0-windows/dump/M_Mole_ElimDevice_Ring/PS_TBasePassPSFNoLightMapPolicy_FNiagaraMeshVertexFactory.asm");
	FString AsmToTranslate;
	if (!FFileHelper::LoadFileToString(AsmToTranslate, *DumpAsmPath) || AsmToTranslate.IsEmpty())
	{
		DumpAsmPath = TEXT("C:/Users/editza/Documents/FUnreal/FModel+UnrealRenderer/UExplorer/bin/Debug/net10.0-windows/dump/M_Mole_ElimDevice_Ring/PS_TBasePassPSFNoLightMapPolicy.asm");
		if (!FFileHelper::LoadFileToString(AsmToTranslate, *DumpAsmPath) || AsmToTranslate.IsEmpty())
		{
			UE_LOG(LogUExplorerShaderTranslator, Warning, TEXT("Could not read ElimDevice dump shader: %s"), *DumpAsmPath);
			return;
		}
	}

	const FString TestFolder = TEXT("/Game/UExplorer/Tests");
	const FString TestMatName = TEXT("M_Mole_ElimDevice_Ring_Test");
	const FString FullPath = FString::Printf(TEXT("%s/%s.%s"), *TestFolder, *TestMatName, *TestMatName);

	UMaterial* TestMat = LoadObject<UMaterial>(nullptr, *FullPath);
	if (!TestMat)
	{
		IAssetTools& AssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
		UMaterialFactoryNew* MatFactory = NewObject<UMaterialFactoryNew>();
		TestMat = Cast<UMaterial>(AssetTools.CreateAsset(TestMatName, TestFolder, UMaterial::StaticClass(), MatFactory));
	}
	else
	{
		UMaterialEditingLibrary::DeleteAllMaterialExpressions(TestMat);
	}

	if (TestMat)
	{
		TestMat->BlendMode = BLEND_Additive;
		TestMat->SetShadingModel(MSM_Unlit);
		TestMat->TwoSided = true;

		TSharedPtr<FJsonObject> EmptyObj = MakeShared<FJsonObject>();
		TMap<FString, UTexture2D*> EmptyTextures;
		TranslateMaterialShader(TestMat, AsmToTranslate, EmptyObj, EmptyTextures);

		UMaterialEditingLibrary::RecompileMaterial(TestMat);
		TestMat->MarkPackageDirty();
		UE_LOG(LogUExplorerShaderTranslator, Log, TEXT("Built 1:1 test material from %s -> %s"), *DumpAsmPath, *FullPath);
	}
}
