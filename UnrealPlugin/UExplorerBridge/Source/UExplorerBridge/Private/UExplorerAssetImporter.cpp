#include "UExplorerAssetImporter.h"
#include "UExplorerShaderTranslator.h"
#include "AssetToolsModule.h"
#include "IAssetTools.h"
#include "AutomatedAssetImportData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Factories/MaterialFactoryNew.h"
#include "Factories/MaterialInstanceConstantFactoryNew.h"
#include "Factories/MaterialParameterCollectionFactoryNew.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Materials/MaterialParameterCollection.h"
#include "Materials/MaterialExpressionTextureBase.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionScalarParameter.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionWorldPosition.h"
#include "Materials/MaterialExpressionConstant3Vector.h"
#include "Materials/MaterialExpressionConstant4Vector.h"
#include "Materials/MaterialExpressionComment.h"
#include "MaterialEditingLibrary.h"
#include "NiagaraSystem.h"
#include "NiagaraEmitter.h"
#include "NiagaraSpriteRendererProperties.h"
#include "NiagaraMeshRendererProperties.h"
#include "NiagaraLightRendererProperties.h"
#include "NiagaraSystemFactoryNew.h"
#include "NiagaraEditorUtilities.h"
#include "NiagaraGraph.h"
#include "NiagaraNodeOutput.h"
#include "NiagaraNodeFunctionCall.h"
#include "NiagaraScriptSource.h"
#include "NiagaraDataInterfaceCurve.h"
#include "NiagaraDataInterfaceColorCurve.h"
#include "ViewModels/Stack/NiagaraStackGraphUtilities.h"
#include "ViewModels/Stack/NiagaraParameterHandle.h"
#include "EdGraphSchema_Niagara.h"
#include "Engine/Texture2D.h"
#include "Engine/StaticMesh.h"
#include "MeshDescription.h"
#include "StaticMeshAttributes.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/PointLight.h"
#include "Engine/SpotLight.h"
#include "Engine/RectLight.h"
#include "Engine/DirectionalLight.h"
#include "Engine/SkyLight.h"
#include "Engine/ExponentialHeightFog.h"
#include "Components/StaticMeshComponent.h"
#include "Components/PointLightComponent.h"
#include "Components/SpotLightComponent.h"
#include "Components/RectLightComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Editor.h"
#include "FileHelpers.h"
#include "NiagaraActor.h"
#include "NiagaraComponent.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonReader.h"
#include "UObject/SavePackage.h"

DEFINE_LOG_CATEGORY_STATIC(LogUExplorerImporter, Log, All);

TSharedPtr<FJsonObject> FUExplorerAssetImporter::LoadJsonObjectFromFile(const FString& FilePath)
{
	FString JsonStr;
	if (!FFileHelper::LoadFileToString(JsonStr, *FilePath))
	{
		UE_LOG(LogUExplorerImporter, Warning, TEXT("Could not read JSON file: %s"), *FilePath);
		return nullptr;
	}
	TSharedPtr<FJsonObject> Obj;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(JsonStr);
	if (!FJsonSerializer::Deserialize(Reader, Obj) || !Obj.IsValid())
	{
		UE_LOG(LogUExplorerImporter, Warning, TEXT("Failed to parse JSON file: %s"), *FilePath);
		return nullptr;
	}
	return Obj;
}

FString FUExplorerAssetImporter::SanitizeAssetName(const FString& InName)
{
	FString Out = InName;
	Out.ReplaceInline(TEXT(" "), TEXT("_"));
	Out.ReplaceInline(TEXT("."), TEXT("_"));
	Out.ReplaceInline(TEXT("-"), TEXT("_"));
	Out.ReplaceInline(TEXT(":"), TEXT("_"));
	return Out.IsEmpty() ? TEXT("UExplorerAsset") : Out;
}

bool FUExplorerAssetImporter::ImportManifestFile(const FString& ManifestPath)
{
	FString JsonText;
	if (!FFileHelper::LoadFileToString(JsonText, *ManifestPath))
	{
		return false;
	}
	return ImportManifestJson(JsonText);
}

bool FUExplorerAssetImporter::ImportManifestJson(const FString& JsonText)
{
	TSharedPtr<FJsonObject> Root;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(JsonText);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return false;
	}

	const FString Kind = Root->GetStringField(TEXT("AssetKind"));
	const TSharedPtr<FJsonObject>* FilesObj = nullptr;
	if (!Root->TryGetObjectField(TEXT("Files"), FilesObj) || !FilesObj)
	{
		return false;
	}

	if (Kind.Equals(TEXT("Material"), ESearchCase::IgnoreCase) || Kind.Equals(TEXT("MaterialInstance"), ESearchCase::IgnoreCase))
	{
		return ImportMaterialFromJson((*FilesObj)->GetStringField(TEXT("MaterialJson")), /*bForceRebuild=*/true) != nullptr;
	}
	if (Kind.Equals(TEXT("StaticMesh"), ESearchCase::IgnoreCase))
	{
		return ImportStaticMeshFromJson((*FilesObj)->GetStringField(TEXT("MeshJson"))) != nullptr;
	}
	if (Kind.Equals(TEXT("Niagara"), ESearchCase::IgnoreCase))
	{
		return ImportNiagaraFromJson((*FilesObj)->GetStringField(TEXT("NiagaraJson")));
	}
	if (Kind.Equals(TEXT("World"), ESearchCase::IgnoreCase))
	{
		return ImportWorldFromJson((*FilesObj)->GetStringField(TEXT("WorldJson")));
	}

	UE_LOG(LogUExplorerImporter, Warning, TEXT("Unhandled AssetKind in manifest: %s"), *Kind);
	return false;
}

UTexture2D* FUExplorerAssetImporter::ImportTexturePng(const FString& PngFilePath, const FString& DestFolder, bool bIsNormalMap, bool bIsMask)
{
	if (!FPaths::FileExists(PngFilePath))
	{
		return nullptr;
	}

	const FString AssetName = SanitizeAssetName(FPaths::GetBaseFilename(PngFilePath));
	const FString FullAssetPath = FString::Printf(TEXT("%s/%s.%s"), *DestFolder, *AssetName, *AssetName);
	if (UTexture2D* Existing = LoadObject<UTexture2D>(nullptr, *FullAssetPath))
	{
		return Existing;
	}

	IAssetTools& AssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
	UAutomatedAssetImportData* ImportData = NewObject<UAutomatedAssetImportData>();
	ImportData->bReplaceExisting = true;
	ImportData->DestinationPath = DestFolder;
	ImportData->Filenames.Add(PngFilePath);

	TArray<UObject*> Imported = AssetTools.ImportAssetsAutomated(ImportData);
	for (UObject* Obj : Imported)
	{
		if (UTexture2D* Tex = Cast<UTexture2D>(Obj))
		{
			if (bIsNormalMap)
			{
				Tex->CompressionSettings = TC_Normalmap;
				Tex->SRGB = false;
			}
			else if (bIsMask)
			{
				Tex->CompressionSettings = TC_Masks;
				Tex->SRGB = false;
			}
			Tex->PostEditChange();
			return Tex;
		}
	}
	return nullptr;
}

UMaterialInterface* FUExplorerAssetImporter::ImportMaterialFromJson(const FString& MaterialJsonPath, bool bForceRebuild)
{
	TSharedPtr<FJsonObject> MatObj = LoadJsonObjectFromFile(MaterialJsonPath);
	if (!MatObj.IsValid())
	{
		return nullptr;
	}

	const FString RawName = MatObj->GetStringField(TEXT("Name"));
	FString AssetName = SanitizeAssetName(RawName);
	bool bIsMid = false;
	MatObj->TryGetBoolField(TEXT("IsMaterialInstanceDynamic"), bIsMid);
	if ((bIsMid || RawName.StartsWith(TEXT("MaterialInstanceDynamic"), ESearchCase::IgnoreCase)))
	{
		// Ensure unique asset name per MID even if an older export didn't hash PackagePath
		FString PkgPath;
		if (!MatObj->TryGetStringField(TEXT("PackagePath"), PkgPath) || PkgPath.IsEmpty())
			PkgPath = MaterialJsonPath;
		const int32 LastUnderscore = AssetName.Find(TEXT("_"), ESearchCase::IgnoreCase, ESearchDir::FromEnd);
		const bool bAlreadyHasHex8 = (LastUnderscore != INDEX_NONE && AssetName.Len() - LastUnderscore - 1 == 8);
		if (!bAlreadyHasHex8)
		{
			uint32 Hash = 2166136261u;
			for (int32 ci = 0; ci < PkgPath.Len(); ++ci)
			{
				Hash ^= static_cast<uint32>(PkgPath[ci]);
				Hash *= 16777619u;
			}
			AssetName = FString::Printf(TEXT("%s_%08X"), *AssetName, Hash);
		}
	}
	const FString DestFolder = TEXT("/Game/UExplorer/Materials");
	const FString TexDestFolder = TEXT("/Game/UExplorer/Textures");
	const FString FullPath = FString::Printf(TEXT("%s/%s.%s"), *DestFolder, *AssetName, *AssetName);

	UMaterialInterface* Existing = LoadObject<UMaterialInterface>(nullptr, *FullPath);
	if (Existing && !bForceRebuild)
	{
		return Existing;
	}

	// 1. Import all referenced PNG textures
	TMap<FString, UTexture2D*> ImportedTextures;
	const TSharedPtr<FJsonObject>* TexturesObj = nullptr;
	if (MatObj->TryGetObjectField(TEXT("Textures"), TexturesObj) && TexturesObj)
	{
		for (const auto& Pair : (*TexturesObj)->Values)
		{
			const FString TexName(Pair.Key);
			const FString PngPath = Pair.Value->AsString();
			const bool bIsNormal = TexName.Contains(TEXT("Normal")) || TexName.EndsWith(TEXT("_N"));
			const bool bIsMask = !bIsNormal && (TexName.Contains(TEXT("_S")) || TexName.Contains(TEXT("Mask")) || TexName.Contains(TEXT("ARM")) || TexName.Contains(TEXT("ORM")));
			if (UTexture2D* Tex = ImportTexturePng(PngPath, TexDestFolder, bIsNormal, bIsMask))
			{
				ImportedTextures.Add(TexName, Tex);
			}
		}
	}

	// 1b. Import any referenced MaterialParameterCollections (MPC) with zero data loss
	const TArray<TSharedPtr<FJsonValue>>* MpcArray = nullptr;
	if (MatObj->TryGetArrayField(TEXT("ParameterCollections"), MpcArray) && MpcArray)
	{
		IAssetTools& MpcAssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
		const FString MpcDestFolder = TEXT("/Game/UExplorer/MPC");
		for (const TSharedPtr<FJsonValue>& MpcVal : *MpcArray)
		{
			const TSharedPtr<FJsonObject> MpcRef = MpcVal->AsObject();
			if (!MpcRef.IsValid()) continue;
			FString MpcJsonPath;
			if (!MpcRef->TryGetStringField(TEXT("MpcJson"), MpcJsonPath) || MpcJsonPath.IsEmpty()) continue;
			TSharedPtr<FJsonObject> MpcObj = LoadJsonObjectFromFile(MpcJsonPath);
			if (!MpcObj.IsValid()) continue;

			const FString MpcName = SanitizeAssetName(MpcObj->GetStringField(TEXT("Name")));
			const FString MpcFullPath = FString::Printf(TEXT("%s/%s.%s"), *MpcDestFolder, *MpcName, *MpcName);
			if (LoadObject<UMaterialParameterCollection>(nullptr, *MpcFullPath)) continue;

			UMaterialParameterCollectionFactoryNew* MpcFactory = NewObject<UMaterialParameterCollectionFactoryNew>();
			if (UMaterialParameterCollection* MPC = Cast<UMaterialParameterCollection>(
				MpcAssetTools.CreateAsset(MpcName, MpcDestFolder, UMaterialParameterCollection::StaticClass(), MpcFactory)))
			{
				FString StateIdStr;
				if (MpcObj->TryGetStringField(TEXT("StateId"), StateIdStr))
					FGuid::Parse(StateIdStr, MPC->StateId);

				const TArray<TSharedPtr<FJsonValue>>* Scalars = nullptr;
				if (MpcObj->TryGetArrayField(TEXT("ScalarParameters"), Scalars) && Scalars)
				{
					for (const TSharedPtr<FJsonValue>& SV : *Scalars)
					{
						if (const TSharedPtr<FJsonObject> SO = SV->AsObject(); SO.IsValid())
						{
							FCollectionScalarParameter P;
							P.ParameterName = FName(*SO->GetStringField(TEXT("Name")));
							P.DefaultValue = static_cast<float>(SO->GetNumberField(TEXT("Value")));
							FString IdStr;
							if (SO->TryGetStringField(TEXT("Id"), IdStr)) FGuid::Parse(IdStr, P.Id);
							if (!P.Id.IsValid()) P.Id = FGuid::NewGuid();
							MPC->ScalarParameters.Add(P);
						}
					}
				}
				const TArray<TSharedPtr<FJsonValue>>* Vectors = nullptr;
				if (MpcObj->TryGetArrayField(TEXT("VectorParameters"), Vectors) && Vectors)
				{
					for (const TSharedPtr<FJsonValue>& VV : *Vectors)
					{
						if (const TSharedPtr<FJsonObject> VO = VV->AsObject(); VO.IsValid())
						{
							FCollectionVectorParameter P;
							P.ParameterName = FName(*VO->GetStringField(TEXT("Name")));
							const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
							if (VO->TryGetArrayField(TEXT("Value"), Arr) && Arr && Arr->Num() >= 4)
								P.DefaultValue = FLinearColor(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()), static_cast<float>((*Arr)[3]->AsNumber()));
							FString IdStr;
							if (VO->TryGetStringField(TEXT("Id"), IdStr)) FGuid::Parse(IdStr, P.Id);
							if (!P.Id.IsValid()) P.Id = FGuid::NewGuid();
							MPC->VectorParameters.Add(P);
						}
					}
				}
				MPC->PostEditChange();
				MPC->MarkPackageDirty();
				UE_LOG(LogUExplorerImporter, Log, TEXT("Imported MPC '%s' (%d scalars, %d vectors)"),
					*MpcName, MPC->ScalarParameters.Num(), MPC->VectorParameters.Num());
			}
		}
	}

	// 2. If this is a Material Instance with an exported ParentMaterialJson, import parent first and create UMaterialInstanceConstant
	IAssetTools& AssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
	bool bIsMaterialInstance = false;
	FString ParentMaterialJson;
	if (MatObj->TryGetBoolField(TEXT("IsMaterialInstance"), bIsMaterialInstance) && bIsMaterialInstance &&
		MatObj->TryGetStringField(TEXT("ParentMaterialJson"), ParentMaterialJson) && !ParentMaterialJson.IsEmpty())
	{
		if (UMaterialInterface* ParentMat = ImportMaterialFromJson(ParentMaterialJson, bForceRebuild))
		{
			UMaterialInstanceConstant* MI = nullptr;
			if (Existing)
			{
				MI = Cast<UMaterialInstanceConstant>(Existing);
				if (!MI)
				{
					UE_LOG(LogUExplorerImporter, Warning, TEXT("Asset '%s' is a UMaterial but re-export expects MaterialInstanceConstant; delete the existing UE asset and re-export."), *FullPath);
					return Existing;
				}
			}
			else
			{
				UMaterialInstanceConstantFactoryNew* MiFactory = NewObject<UMaterialInstanceConstantFactoryNew>();
				MiFactory->InitialParent = ParentMat;
				MI = Cast<UMaterialInstanceConstant>(
					AssetTools.CreateAsset(AssetName, DestFolder, UMaterialInstanceConstant::StaticClass(), MiFactory));
			}

			if (MI)
			{
				MI->SetParentEditorOnly(ParentMat);

				const TSharedPtr<FJsonObject>* ScalarOverrides = nullptr;
				if (MatObj->TryGetObjectField(TEXT("InstanceScalarOverrides"), ScalarOverrides) && ScalarOverrides)
				{
					for (const auto& Pair : (*ScalarOverrides)->Values)
					{
						const FString ParamName(Pair.Key);
						MI->SetScalarParameterValueEditorOnly(FMaterialParameterInfo(FName(*ParamName)), static_cast<float>(Pair.Value->AsNumber()));
					}
				}

				const TSharedPtr<FJsonObject>* VectorOverrides = nullptr;
				if (MatObj->TryGetObjectField(TEXT("InstanceVectorOverrides"), VectorOverrides) && VectorOverrides)
				{
					for (const auto& Pair : (*VectorOverrides)->Values)
					{
						const FString ParamName(Pair.Key);
						const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
						if (Pair.Value->TryGetArray(Arr) && Arr && Arr->Num() >= 3)
						{
							const float R = static_cast<float>((*Arr)[0]->AsNumber());
							const float G = static_cast<float>((*Arr)[1]->AsNumber());
							const float B = static_cast<float>((*Arr)[2]->AsNumber());
							const float A = (Arr->Num() >= 4) ? static_cast<float>((*Arr)[3]->AsNumber()) : 1.0f;
							MI->SetVectorParameterValueEditorOnly(FMaterialParameterInfo(FName(*ParamName)), FLinearColor(R, G, B, A));
						}
					}
				}

				const TSharedPtr<FJsonObject>* TextureOverrides = nullptr;
				if (MatObj->TryGetObjectField(TEXT("InstanceTextureOverrides"), TextureOverrides) && TextureOverrides)
				{
					for (const auto& Pair : (*TextureOverrides)->Values)
					{
						const FString ParamName(Pair.Key);
						const FString TexName = Pair.Value->AsString();
						if (UTexture2D** FoundTex = ImportedTextures.Find(TexName))
						{
							MI->SetTextureParameterValueEditorOnly(FMaterialParameterInfo(FName(*ParamName)), *FoundTex);
						}
					}
				}

				MI->PostEditChange();
				MI->MarkPackageDirty();
				UE_LOG(LogUExplorerImporter, Log, TEXT("Imported Material Instance '%s' (Parent: '%s')"), *AssetName, *ParentMat->GetName());
				return MI;
			}
		}
	}

	// 3. Create or rebuild UMaterial asset
	UMaterial* NewMat = nullptr;
	if (Existing)
	{
		NewMat = Cast<UMaterial>(Existing);
		if (!NewMat)
		{
			UE_LOG(LogUExplorerImporter, Warning, TEXT("Asset '%s' is a MaterialInstanceConstant but re-export expects UMaterial (static permutation); delete the old UE asset and re-export."), *FullPath);
			return Existing;
		}
		UMaterialEditingLibrary::DeleteAllMaterialExpressions(NewMat);
	}
	else
	{
		UMaterialFactoryNew* MatFactory = NewObject<UMaterialFactoryNew>();
		NewMat = Cast<UMaterial>(AssetTools.CreateAsset(AssetName, DestFolder, UMaterial::StaticClass(), MatFactory));
	}
	if (!NewMat)
	{
		return nullptr;
	}

	const FString BlendModeStr = MatObj->GetStringField(TEXT("BlendMode"));
	if (BlendModeStr.Contains(TEXT("Masked"))) NewMat->BlendMode = BLEND_Masked;
	else if (BlendModeStr.Contains(TEXT("Translucent"))) NewMat->BlendMode = BLEND_Translucent;
	else if (BlendModeStr.Contains(TEXT("Additive"))) NewMat->BlendMode = BLEND_Additive;
	else if (BlendModeStr.Contains(TEXT("Modulate"))) NewMat->BlendMode = BLEND_Modulate;

	const FString ShadingModelStr = MatObj->GetStringField(TEXT("ShadingModel"));
	if (ShadingModelStr.Contains(TEXT("Unlit"))) NewMat->SetShadingModel(MSM_Unlit);
	else if (ShadingModelStr.Contains(TEXT("SubsurfaceProfile"))) NewMat->SetShadingModel(MSM_SubsurfaceProfile);
	else if (ShadingModelStr.Contains(TEXT("Subsurface"))) NewMat->SetShadingModel(MSM_Subsurface);
	else if (ShadingModelStr.Contains(TEXT("ClearCoat"))) NewMat->SetShadingModel(MSM_ClearCoat);
	else if (ShadingModelStr.Contains(TEXT("TwoSidedFoliage"))) NewMat->SetShadingModel(MSM_TwoSidedFoliage);

	NewMat->TwoSided = MatObj->GetBoolField(TEXT("TwoSided"));

	// Prefer native C++ DXBC PixelShaderAsm -> UE5 Material Expression translator when available
	FString PixelShaderAsm;
	if (MatObj->TryGetStringField(TEXT("PixelShaderAsm"), PixelShaderAsm) && !PixelShaderAsm.IsEmpty())
	{
		if (FUExplorerShaderTranslator::TranslateMaterialShader(NewMat, PixelShaderAsm, MatObj, ImportedTextures))
		{
			UMaterialEditingLibrary::RecompileMaterial(NewMat);
			NewMat->MarkPackageDirty();
			return NewMat;
		}
	}

	auto MapRootPin = [](const FString& PinName, EMaterialProperty& OutProp) -> bool
	{
		if (PinName.Equals(TEXT("Base Color"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("BaseColor"), ESearchCase::IgnoreCase)) { OutProp = MP_BaseColor; return true; }
		if (PinName.Equals(TEXT("Metallic"), ESearchCase::IgnoreCase)) { OutProp = MP_Metallic; return true; }
		if (PinName.Equals(TEXT("Specular"), ESearchCase::IgnoreCase)) { OutProp = MP_Specular; return true; }
		if (PinName.Equals(TEXT("Roughness"), ESearchCase::IgnoreCase)) { OutProp = MP_Roughness; return true; }
		if (PinName.Equals(TEXT("Anisotropy"), ESearchCase::IgnoreCase)) { OutProp = MP_Anisotropy; return true; }
		if (PinName.Equals(TEXT("Emissive Color"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("Emissive"), ESearchCase::IgnoreCase)) { OutProp = MP_EmissiveColor; return true; }
		if (PinName.Equals(TEXT("Opacity"), ESearchCase::IgnoreCase)) { OutProp = MP_Opacity; return true; }
		if (PinName.Equals(TEXT("Opacity Mask"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("OpacityMask"), ESearchCase::IgnoreCase)) { OutProp = MP_OpacityMask; return true; }
		if (PinName.Equals(TEXT("Normal"), ESearchCase::IgnoreCase)) { OutProp = MP_Normal; return true; }
		if (PinName.Equals(TEXT("Tangent"), ESearchCase::IgnoreCase)) { OutProp = MP_Tangent; return true; }
		if (PinName.Equals(TEXT("World Position Offset"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("WPO"), ESearchCase::IgnoreCase)) { OutProp = MP_WorldPositionOffset; return true; }
		if (PinName.Equals(TEXT("Ambient Occlusion"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("AmbientOcclusion"), ESearchCase::IgnoreCase)) { OutProp = MP_AmbientOcclusion; return true; }
		if (PinName.Equals(TEXT("Subsurface Color"), ESearchCase::IgnoreCase) || PinName.Equals(TEXT("Subsurface"), ESearchCase::IgnoreCase)) { OutProp = MP_SubsurfaceColor; return true; }
		if (PinName.Equals(TEXT("Refraction"), ESearchCase::IgnoreCase)) { OutProp = MP_Refraction; return true; }
		if (PinName.Equals(TEXT("Pixel Depth Offset"), ESearchCase::IgnoreCase)) { OutProp = MP_PixelDepthOffset; return true; }
		return false;
	};

	// 3. Reconstruct 1:1 UE5 Material Expression graph from GraphNodes + GraphLinks
	TMap<FString, UMaterialExpression*> CreatedNodes;
	const TArray<TSharedPtr<FJsonValue>>* GraphNodes = nullptr;
	if (MatObj->TryGetArrayField(TEXT("GraphNodes"), GraphNodes) && GraphNodes && GraphNodes->Num() > 0)
	{
		for (const TSharedPtr<FJsonValue>& Val : *GraphNodes)
		{
			const TSharedPtr<FJsonObject> NodeObj = Val->AsObject();
			if (!NodeObj.IsValid()) continue;

			const FString NodeName = NodeObj->GetStringField(TEXT("NodeName"));
			const FString ExprClassName = NodeObj->GetStringField(TEXT("ExpressionClass"));
			const int32 PosX = static_cast<int32>(NodeObj->GetNumberField(TEXT("PosX")));
			const int32 PosY = static_cast<int32>(NodeObj->GetNumberField(TEXT("PosY")));

			if (ExprClassName.IsEmpty()) continue;

			const FString ClassPath = ExprClassName.StartsWith(TEXT("/"))
				? ExprClassName
				: FString::Printf(TEXT("/Script/Engine.%s"), *ExprClassName);

			UClass* ExprClass = StaticLoadClass(UMaterialExpression::StaticClass(), nullptr, *ClassPath);
			if (!ExprClass)
			{
				UE_LOG(LogUExplorerImporter, Warning, TEXT("Skipping unknown material expression class: %s"), *ClassPath);
				continue;
			}

			UMaterialExpression* Expr = UMaterialEditingLibrary::CreateMaterialExpression(NewMat, ExprClass, PosX, PosY);
			if (!Expr) continue;

			// Apply all exported UE expression properties via reflection
			const TSharedPtr<FJsonObject>* PropsObj = nullptr;
			if (NodeObj->TryGetObjectField(TEXT("Properties"), PropsObj) && PropsObj)
			{
				for (const auto& PropPair : (*PropsObj)->Values)
				{
					const FString PropKey(PropPair.Key);
					if (PropKey.Equals(TEXT("Texture"), ESearchCase::IgnoreCase) ||
						PropKey.Equals(TEXT("MaterialExpressionGuid"), ESearchCase::IgnoreCase))
					{
						continue;
					}

					if (FProperty* Prop = Expr->GetClass()->FindPropertyByName(FName(*PropKey)))
					{
						const FString PropVal = PropPair.Value->AsString();
						Prop->ImportText_Direct(*PropVal, Prop->ContainerPtrToValuePtr<void>(Expr), Expr, PPF_None);
					}
				}
			}

			// Assign imported UTexture2D if this is a texture sampler node
			FString TexName;
			if (NodeObj->TryGetStringField(TEXT("TextureName"), TexName) && !TexName.IsEmpty())
			{
				if (UTexture2D** FoundTex = ImportedTextures.Find(TexName))
				{
					if (UMaterialExpressionTextureBase* TexBase = Cast<UMaterialExpressionTextureBase>(Expr))
					{
						TexBase->Texture = *FoundTex;
						if ((*FoundTex)->CompressionSettings == TC_Normalmap)
							TexBase->SamplerType = SAMPLERTYPE_Normal;
						else if ((*FoundTex)->CompressionSettings == TC_Masks)
							TexBase->SamplerType = SAMPLERTYPE_Masks;
					}
				}
			}

			CreatedNodes.Add(NodeName, Expr);
		}

		// Connect all node-to-node and node-to-root pins with smart fallback for UE5 unnamed pins
		auto ConnectPropertySmart = [](UMaterialExpression* FromExpr, const FString& FromPin, EMaterialProperty MatProp) -> bool
		{
			if (UMaterialEditingLibrary::ConnectMaterialProperty(FromExpr, FromPin, MatProp)) return true;
			if (!FromPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialProperty(FromExpr, TEXT(""), MatProp)) return true;
			return false;
		};

		auto ConnectExpressionsSmart = [NewMat](UMaterialExpression* FromExpr, const FString& FromPin, UMaterialExpression* ToExpr, const FString& ToPin) -> bool
		{
			// Guard TextureSample UVs/Coordinates against float3/LWCVector3 -> float2 SM6 compile errors
			const bool bIsTexCoordPin = Cast<UMaterialExpressionTextureBase>(ToExpr) != nullptr &&
				(ToPin.Equals(TEXT("UVs"), ESearchCase::IgnoreCase) || ToPin.Equals(TEXT("Coordinates"), ESearchCase::IgnoreCase));

			UMaterialExpression* EffectiveFrom = FromExpr;
			FString EffectiveFromPin = FromPin;

			if (bIsTexCoordPin)
			{
				const bool bIs3Or4Chan =
					FromExpr->IsA<UMaterialExpressionWorldPosition>() ||
					FromExpr->IsA<UMaterialExpressionVectorParameter>() ||
					FromExpr->IsA<UMaterialExpressionConstant3Vector>() ||
					FromExpr->IsA<UMaterialExpressionConstant4Vector>();
				const bool bIsFullVectorPin = FromPin.IsEmpty() ||
					FromPin.Equals(TEXT("Output"), ESearchCase::IgnoreCase) ||
					FromPin.Equals(TEXT("RGB"), ESearchCase::IgnoreCase) ||
					FromPin.Equals(TEXT("RGBA"), ESearchCase::IgnoreCase);

				if (bIs3Or4Chan && bIsFullVectorPin)
				{
					if (UMaterialExpressionComponentMask* Mask = Cast<UMaterialExpressionComponentMask>(
						UMaterialEditingLibrary::CreateMaterialExpression(NewMat, UMaterialExpressionComponentMask::StaticClass(),
							(FromExpr->MaterialExpressionEditorX + ToExpr->MaterialExpressionEditorX) / 2,
							ToExpr->MaterialExpressionEditorY)))
					{
						Mask->R = 1; Mask->G = 1; Mask->B = 0; Mask->A = 0;
						if (!UMaterialEditingLibrary::ConnectMaterialExpressions(FromExpr, FromPin, Mask, TEXT("")))
							UMaterialEditingLibrary::ConnectMaterialExpressions(FromExpr, TEXT(""), Mask, TEXT(""));
						EffectiveFrom = Mask;
						EffectiveFromPin = TEXT("");
					}
				}

				if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, EffectiveFromPin, ToExpr, TEXT("UVs"))) return true;
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, TEXT(""), ToExpr, TEXT("UVs"))) return true;
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, EffectiveFromPin, ToExpr, TEXT("Coordinates"))) return true;
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, TEXT(""), ToExpr, TEXT("Coordinates"))) return true;
				return false;
			}

			if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, EffectiveFromPin, ToExpr, ToPin)) return true;
			if (!EffectiveFromPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, TEXT(""), ToExpr, ToPin)) return true;

			// Only fall back to unnamed input pin ("") for unary/generic input pin names — never hijack multi-input nodes like TextureSample
			const bool bIsUnaryInputName = ToPin.IsEmpty() ||
				ToPin.Equals(TEXT("Input"), ESearchCase::IgnoreCase) ||
				ToPin.Equals(TEXT("Value"), ESearchCase::IgnoreCase) ||
				ToPin.Equals(TEXT("In"), ESearchCase::IgnoreCase) ||
				ToPin.Equals(TEXT("X"), ESearchCase::IgnoreCase);

			if (bIsUnaryInputName && !Cast<UMaterialExpressionTextureBase>(ToExpr))
			{
				if (UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, EffectiveFromPin, ToExpr, TEXT(""))) return true;
				if (!EffectiveFromPin.IsEmpty() && UMaterialEditingLibrary::ConnectMaterialExpressions(EffectiveFrom, TEXT(""), ToExpr, TEXT(""))) return true;
			}
			return false;
		};

		const TArray<TSharedPtr<FJsonValue>>* GraphLinks = nullptr;
		if (MatObj->TryGetArrayField(TEXT("GraphLinks"), GraphLinks) && GraphLinks)
		{
			for (const TSharedPtr<FJsonValue>& Val : *GraphLinks)
			{
				const TSharedPtr<FJsonObject> LinkObj = Val->AsObject();
				if (!LinkObj.IsValid()) continue;

				const FString FromNode = LinkObj->GetStringField(TEXT("FromNode"));
				const FString FromPin = LinkObj->GetStringField(TEXT("FromPin"));
				const FString ToNode = LinkObj->GetStringField(TEXT("ToNode"));
				const FString ToPin = LinkObj->GetStringField(TEXT("ToPin"));

				UMaterialExpression* FromExpr = CreatedNodes.FindRef(FromNode);
				if (!FromExpr) continue;

				if (ToNode.Equals(TEXT("Root"), ESearchCase::IgnoreCase))
				{
					EMaterialProperty MatProp = MP_BaseColor;
					if (MapRootPin(ToPin, MatProp))
					{
						ConnectPropertySmart(FromExpr, FromPin, MatProp);
					}
				}
				else if (UMaterialExpression* ToExpr = CreatedNodes.FindRef(ToNode))
				{
					ConnectExpressionsSmart(FromExpr, FromPin, ToExpr, ToPin);
				}
			}
		}
	}

	// Fallback: if no graph nodes were reconstructed, wire imported textures into standard PBR pins
	if (CreatedNodes.Num() == 0)
	{
		int32 NodeY = -300;
		for (const auto& Pair : ImportedTextures)
		{
			const FString& TexName = Pair.Key;
			UTexture2D* Tex = Pair.Value;
			UMaterialExpressionTextureSampleParameter2D* TexExpr = Cast<UMaterialExpressionTextureSampleParameter2D>(
				UMaterialEditingLibrary::CreateMaterialExpression(NewMat, UMaterialExpressionTextureSampleParameter2D::StaticClass(), -480, NodeY));
			if (!TexExpr) continue;

			TexExpr->ParameterName = FName(*TexName);
			TexExpr->Texture = Tex;
			if (Tex->CompressionSettings == TC_Normalmap)
			{
				TexExpr->SamplerType = SAMPLERTYPE_Normal;
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("RGB"), MP_Normal);
			}
			else if (Tex->CompressionSettings == TC_Masks)
			{
				TexExpr->SamplerType = SAMPLERTYPE_Masks;
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("R"), MP_Specular);
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("G"), MP_Metallic);
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("B"), MP_Roughness);
			}
			else if (TexName.Contains(TEXT("Emissive")))
			{
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("RGB"), MP_EmissiveColor);
			}
			else
			{
				UMaterialEditingLibrary::ConnectMaterialProperty(TexExpr, TEXT("RGB"), MP_BaseColor);
			}
			NodeY += 240;
		}
	}

	UMaterialEditingLibrary::RecompileMaterial(NewMat);
	const FMaterialStatistics MatStats = UMaterialEditingLibrary::GetStatistics(NewMat);
	if (MatStats.NumPixelShaderInstructions <= 0)
	{
		UE_LOG(LogTemp, Error, TEXT("[UExplorerBridge] Material '%s' failed to compile (0 PS instructions)."), *NewMat->GetName());
	}
	else
	{
		UE_LOG(LogTemp, Display, TEXT("[UExplorerBridge] Material '%s' compiled: VS=%d PS=%d Samplers=%d TexSamples=%d"),
			*NewMat->GetName(), MatStats.NumVertexShaderInstructions, MatStats.NumPixelShaderInstructions,
			MatStats.NumSamplers, MatStats.NumPixelTextureSamples);
	}
	NewMat->MarkPackageDirty();
	return NewMat;
}

UStaticMesh* FUExplorerAssetImporter::ImportStaticMeshFromJson(const FString& MeshJsonPath)
{
	TSharedPtr<FJsonObject> MeshObj = LoadJsonObjectFromFile(MeshJsonPath);
	if (!MeshObj.IsValid())
	{
		return nullptr;
	}

	const FString RawName = MeshObj->GetStringField(TEXT("Name"));
	const FString AssetName = SanitizeAssetName(RawName);
	const FString ObjFile = MeshObj->GetStringField(TEXT("ObjFile"));
	const FString DestFolder = TEXT("/Game/UExplorer/Meshes");
	const FString FullPath = FString::Printf(TEXT("%s/%s.%s"), *DestFolder, *AssetName, *AssetName);

	UStaticMesh* Mesh = LoadObject<UStaticMesh>(nullptr, *FullPath);
	if (!Mesh && FPaths::FileExists(ObjFile))
	{
		IAssetTools& AssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
		UAutomatedAssetImportData* ImportData = NewObject<UAutomatedAssetImportData>();
		ImportData->bReplaceExisting = true;
		ImportData->DestinationPath = DestFolder;
		ImportData->Filenames.Add(ObjFile);

		TArray<UObject*> Imported = AssetTools.ImportAssetsAutomated(ImportData);
		for (UObject* Obj : Imported)
		{
			if (UStaticMesh* SM = Cast<UStaticMesh>(Obj))
			{
				Mesh = SM;
				break;
			}
		}
	}

	if (!Mesh)
	{
		return nullptr;
	}

	// Apply extra UV channels (UV1+, e.g. BaseSimple_4Layers layer index) & VertexColors from mesh_attribs.bin
	FString AttribsFile;
	if (MeshObj->TryGetStringField(TEXT("AttribsFile"), AttribsFile) && !AttribsFile.IsEmpty() && FPaths::FileExists(AttribsFile))
	{
		TArray<uint8> Bytes;
		if (FFileHelper::LoadFileToArray(Bytes, *AttribsFile) && Bytes.Num() >= 20)
		{
			const uint8* Ptr = Bytes.GetData();
			auto ReadI32 = [&](int32& Off) -> int32 { int32 V = 0; FMemory::Memcpy(&V, Ptr + Off, 4); Off += 4; return V; };
			int32 Off = 0;
			const uint32 Magic = static_cast<uint32>(ReadI32(Off));
			const int32 VCount = ReadI32(Off);
			const int32 ExtraUvCount = ReadI32(Off);
			const int32 HasColors = ReadI32(Off);
			const int32 ICount = ReadI32(Off);
			const int32 ExpectedSize = 20 + ExtraUvCount * VCount * 8 + (HasColors ? VCount * 4 : 0) + ICount * 4;
			if (Magic == 0x4D415452u && VCount > 0 && ICount > 0 && Bytes.Num() >= ExpectedSize)
			{
				const float* UvData = reinterpret_cast<const float*>(Ptr + Off);
				Off += ExtraUvCount * VCount * 8;
				const uint8* ColorData = HasColors ? (Ptr + Off) : nullptr;
				if (HasColors) Off += VCount * 4;
				const int32* IndicesData = reinterpret_cast<const int32*>(Ptr + Off);

				if (FMeshDescription* MD = Mesh->GetMeshDescription(0))
				{
					const int32 NumTris = MD->Triangles().Num();
					if (NumTris * 3 == ICount)
					{
						FStaticMeshAttributes Attrs(*MD);
						TVertexInstanceAttributesRef<FVector2f> UVs = Attrs.GetVertexInstanceUVs();
						TVertexInstanceAttributesRef<FVector4f> Colors = Attrs.GetVertexInstanceColors();
						if (ExtraUvCount > 0 && UVs.GetNumChannels() < 1 + ExtraUvCount)
						{
							UVs.SetNumChannels(1 + ExtraUvCount);
						}

						int32 CornerIdx = 0;
						for (const FTriangleID TriID : MD->Triangles().GetElementIDs())
						{
							TArrayView<const FVertexInstanceID> TriInsts = MD->GetTriangleVertexInstances(TriID);
							for (int32 k = 0; k < 3; ++k)
							{
								const int32 VIdx = IndicesData[CornerIdx++];
								if (VIdx >= 0 && VIdx < VCount)
								{
									const FVertexInstanceID InstID = TriInsts[k];
									for (int32 c = 0; c < ExtraUvCount; ++c)
									{
										const int32 Base = (c * VCount + VIdx) * 2;
										UVs.Set(InstID, 1 + c, FVector2f(UvData[Base], UvData[Base + 1]));
									}
									if (ColorData)
									{
										const uint8* C = ColorData + VIdx * 4;
										Colors[InstID] = FVector4f(FLinearColor::FromSRGBColor(FColor(C[0], C[1], C[2], C[3])));
									}
								}
							}
						}
						Mesh->CommitMeshDescription(0);
						Mesh->Build(false);
					}
					else
					{
						UE_LOG(LogUExplorerImporter, Warning, TEXT("StaticMesh '%s' triangle count mismatch (%d*3 vs %d), skipping extra attribs"), *AssetName, NumTris, ICount);
					}
				}
			}
		}
	}

	// Import & assign all slot materials (and their textures!)
	const TArray<TSharedPtr<FJsonValue>>* MatsArr = nullptr;
	if (MeshObj->TryGetArrayField(TEXT("Materials"), MatsArr) && MatsArr)
	{
		for (const TSharedPtr<FJsonValue>& Val : *MatsArr)
		{
			const TSharedPtr<FJsonObject> SlotObj = Val->AsObject();
			if (!SlotObj.IsValid()) continue;
			const int32 SlotIdx = SlotObj->GetIntegerField(TEXT("SlotIndex"));
			const FString MatJson = SlotObj->GetStringField(TEXT("MaterialJson"));
			if (UMaterialInterface* Mat = ImportMaterialFromJson(MatJson))
			{
				Mesh->SetMaterial(SlotIdx, Mat);
			}
		}
		Mesh->PostEditChange();
		Mesh->MarkPackageDirty();
	}

	return Mesh;
}

bool FUExplorerAssetImporter::ImportNiagaraFromJson(const FString& NiagaraJsonPath)
{
	TSharedPtr<FJsonObject> NObj = LoadJsonObjectFromFile(NiagaraJsonPath);
	if (!NObj.IsValid())
	{
		return false;
	}

	const FString TexDest = TEXT("/Game/UExplorer/Niagara/Textures");

	struct FFloatCurveKey { float Time = 0.0f; float Value = 1.0f; };
	struct FColorCurveKey { float Time = 0.0f; FLinearColor Color = FLinearColor::White; };

	auto ParseFloatCurve = [](const TSharedPtr<FJsonObject>& Obj, const TCHAR* FieldName, TArray<FFloatCurveKey>& OutKeys)
	{
		const TSharedPtr<FJsonObject>* CurveObj = nullptr;
		if (!Obj->TryGetObjectField(FieldName, CurveObj) || !CurveObj || !CurveObj->IsValid()) return;
		const TArray<TSharedPtr<FJsonValue>>* Keys = nullptr;
		if (!(*CurveObj)->TryGetArrayField(TEXT("Keys"), Keys) || !Keys) return;
		for (const TSharedPtr<FJsonValue>& KV : *Keys)
		{
			const TSharedPtr<FJsonObject> KO = KV->AsObject();
			if (!KO.IsValid()) continue;
			FFloatCurveKey K;
			double T = 0.0, Val = 1.0;
			if (KO->TryGetNumberField(TEXT("Time"), T)) K.Time = static_cast<float>(T);
			if (KO->TryGetNumberField(TEXT("Value"), Val)) K.Value = static_cast<float>(Val);
			OutKeys.Add(K);
		}
	};

	auto ParseColorCurve = [](const TSharedPtr<FJsonObject>& Obj, const TCHAR* FieldName, TArray<FColorCurveKey>& OutKeys)
	{
		const TSharedPtr<FJsonObject>* CurveObj = nullptr;
		if (!Obj->TryGetObjectField(FieldName, CurveObj) || !CurveObj || !CurveObj->IsValid()) return;
		const TArray<TSharedPtr<FJsonValue>>* Keys = nullptr;
		if (!(*CurveObj)->TryGetArrayField(TEXT("Keys"), Keys) || !Keys) return;
		for (const TSharedPtr<FJsonValue>& KV : *Keys)
		{
			const TSharedPtr<FJsonObject> KO = KV->AsObject();
			if (!KO.IsValid()) continue;
			FColorCurveKey K;
			double T = 0.0, R = 1.0, G = 1.0, B = 1.0, A = 1.0;
			if (KO->TryGetNumberField(TEXT("Time"), T)) K.Time = static_cast<float>(T);
			if (KO->TryGetNumberField(TEXT("R"), R)) K.Color.R = static_cast<float>(R);
			if (KO->TryGetNumberField(TEXT("G"), G)) K.Color.G = static_cast<float>(G);
			if (KO->TryGetNumberField(TEXT("B"), B)) K.Color.B = static_cast<float>(B);
			if (KO->TryGetNumberField(TEXT("A"), A)) K.Color.A = static_cast<float>(A);
			OutKeys.Add(K);
		}
	};

	struct FImportedSpriteEmitter
	{
		FString Name;
		bool bEnabled = true;
		bool bLocalSpace = false;
		float SpawnRate = 10.0f;
		float Lifetime = 2.0f;
		float SpawnRadius = 0.0f;
		float VerticalLineLength = 0.0f;
		float TurbulenceStrength = 0.0f;
		float SpriteRotation = 0.0f;
		FVector3f OriginOffset = FVector3f::ZeroVector;
		FVector3f BoxSize = FVector3f::ZeroVector;
		FVector3f BoxCenter = FVector3f::ZeroVector;
		FVector3f VelAvg = FVector3f::ZeroVector;
		FVector2f SpriteSize = FVector2f(20.0f, 20.0f);
		FLinearColor Color = FLinearColor::White;
		float VelocitySpeed = 0.0f;
		float GravityZ = 0.0f;
		UMaterialInterface* Material = nullptr;
		FVector2D SubImageSize = FVector2D(1.0, 1.0);
		FVector2D PivotInUVSpace = FVector2D(0.5, 0.5);
		bool bSubImageBlend = false;
		TArray<FColorCurveKey> ColorCurve;
		TArray<FFloatCurveKey> ScaleCurve;
		TArray<FFloatCurveKey> AlphaCurve;
	};
	struct FImportedMeshEmitter
	{
		FString Name;
		bool bEnabled = true;
		bool bLocalSpace = true;
		float SpawnRate = 2.0f;
		float Lifetime = 3.0f;
		FVector3f Scale = FVector3f(1.0f, 1.0f, 1.0f);
		FVector3f RotationEuler = FVector3f::ZeroVector;
		FVector3f SpinSpeedEuler = FVector3f::ZeroVector;
		FVector3f TravelVector = FVector3f::ZeroVector;
		FVector3f BoxSize = FVector3f::ZeroVector;
		FVector3f BoxCenter = FVector3f::ZeroVector;
		FVector3f VelAvg = FVector3f::ZeroVector;
		FLinearColor Color = FLinearColor::White;
		float GravityZ = 0.0f;
		int32 FacingMode = 0;
		bool bLockedAxisEnable = false;
		FVector3f LockedAxis = FVector3f(0.0f, 0.0f, 1.0f);
		int32 LockedAxisSpace = 0;
		FVector2f PosZRange = FVector2f::ZeroVector;
		float DynamicParam3 = 0.0f;
		UStaticMesh* Mesh = nullptr;
		UMaterialInterface* Material = nullptr;
		TArray<FColorCurveKey> ColorCurve;
		TArray<FFloatCurveKey> ScaleCurve;
		TArray<FFloatCurveKey> ScaleRgbCurve;
		TArray<FFloatCurveKey> AlphaCurve;
		TArray<FFloatCurveKey> PositionCurve;
		TArray<FFloatCurveKey> DynamicParam1Curve;
		TArray<FFloatCurveKey> DynamicParam2Curve;
	};
	struct FImportedLightEmitter
	{
		FString Name;
		bool bEnabled = true;
		FLinearColor Color = FLinearColor::White;
		float Intensity = 5.0f;
		float Radius = 500.0f;
		float RadiusScale = 1.0f;
	};

	TArray<FImportedSpriteEmitter> ImportedSprites;
	const TArray<TSharedPtr<FJsonValue>>* Sprites = nullptr;
	if (NObj->TryGetArrayField(TEXT("SpriteEmitters"), Sprites) && Sprites)
	{
		for (const TSharedPtr<FJsonValue>& V : *Sprites)
		{
			const TSharedPtr<FJsonObject> S = V->AsObject();
			if (!S.IsValid()) continue;

			FImportedSpriteEmitter Entry;
			S->TryGetStringField(TEXT("EmitterName"), Entry.Name);
			S->TryGetBoolField(TEXT("Enabled"), Entry.bEnabled);
			S->TryGetBoolField(TEXT("LocalSpace"), Entry.bLocalSpace);
			double Num = 0.0;
			if (S->TryGetNumberField(TEXT("SpawnRate"), Num)) Entry.SpawnRate = FMath::Max(0.1f, static_cast<float>(Num));
			if (S->TryGetNumberField(TEXT("Lifetime"), Num)) Entry.Lifetime = FMath::Max(0.05f, static_cast<float>(Num));
			if (S->TryGetNumberField(TEXT("SpawnRadius"), Num)) Entry.SpawnRadius = static_cast<float>(Num);
			if (S->TryGetNumberField(TEXT("VerticalLineLength"), Num)) Entry.VerticalLineLength = static_cast<float>(Num);
			if (S->TryGetNumberField(TEXT("TurbulenceStrength"), Num)) Entry.TurbulenceStrength = static_cast<float>(Num);
			if (S->TryGetNumberField(TEXT("SpriteRotation"), Num)) Entry.SpriteRotation = static_cast<float>(Num);
			if (S->TryGetNumberField(TEXT("VelocitySpeed"), Num)) Entry.VelocitySpeed = static_cast<float>(Num);
			if (S->TryGetNumberField(TEXT("GravityZ"), Num)) Entry.GravityZ = static_cast<float>(Num);
			S->TryGetBoolField(TEXT("SubImageBlend"), Entry.bSubImageBlend);

			const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
			if (S->TryGetArrayField(TEXT("OriginOffset"), Arr) && Arr && Arr->Num() >= 3)
				Entry.OriginOffset = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (S->TryGetArrayField(TEXT("BoxSize"), Arr) && Arr && Arr->Num() >= 3)
				Entry.BoxSize = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (S->TryGetArrayField(TEXT("BoxCenter"), Arr) && Arr && Arr->Num() >= 3)
				Entry.BoxCenter = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (S->TryGetArrayField(TEXT("VelAvg"), Arr) && Arr && Arr->Num() >= 3)
				Entry.VelAvg = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (S->TryGetArrayField(TEXT("SpriteSize"), Arr) && Arr && Arr->Num() >= 2)
				Entry.SpriteSize = FVector2f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()));
			if (S->TryGetArrayField(TEXT("Color"), Arr) && Arr && Arr->Num() >= 4)
				Entry.Color = FLinearColor(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()), static_cast<float>((*Arr)[3]->AsNumber()));

			ParseColorCurve(S, TEXT("ColorCurve"), Entry.ColorCurve);
			ParseFloatCurve(S, TEXT("ScaleCurve"), Entry.ScaleCurve);
			ParseFloatCurve(S, TEXT("AlphaCurve"), Entry.AlphaCurve);

			FString TexFile;
			if (S->TryGetStringField(TEXT("TextureFile"), TexFile) && !TexFile.IsEmpty())
				ImportTexturePng(TexFile, TexDest);

			FString MatJson;
			if (S->TryGetStringField(TEXT("MaterialJson"), MatJson) && !MatJson.IsEmpty())
				Entry.Material = ImportMaterialFromJson(MatJson);

			const TArray<TSharedPtr<FJsonValue>>* SubImg = nullptr;
			if (S->TryGetArrayField(TEXT("SubImageSize"), SubImg) && SubImg && SubImg->Num() >= 2)
			{
				Entry.SubImageSize = FVector2D(
					FMath::Max(1.0, (*SubImg)[0]->AsNumber()),
					FMath::Max(1.0, (*SubImg)[1]->AsNumber()));
			}
			const TArray<TSharedPtr<FJsonValue>>* PivotArr = nullptr;
			if (S->TryGetArrayField(TEXT("PivotInUVSpace"), PivotArr) && PivotArr && PivotArr->Num() >= 2)
			{
				Entry.PivotInUVSpace = FVector2D((*PivotArr)[0]->AsNumber(), (*PivotArr)[1]->AsNumber());
			}

			ImportedSprites.Add(Entry);
		}
	}

	TArray<FImportedMeshEmitter> ImportedMeshes;
	const TArray<TSharedPtr<FJsonValue>>* Meshes = nullptr;
	if (NObj->TryGetArrayField(TEXT("MeshEmitters"), Meshes) && Meshes)
	{
		for (const TSharedPtr<FJsonValue>& V : *Meshes)
		{
			const TSharedPtr<FJsonObject> M = V->AsObject();
			if (!M.IsValid()) continue;

			FImportedMeshEmitter Entry;
			M->TryGetStringField(TEXT("EmitterName"), Entry.Name);
			M->TryGetBoolField(TEXT("Enabled"), Entry.bEnabled);
			M->TryGetBoolField(TEXT("LocalSpace"), Entry.bLocalSpace);
			double Num = 0.0;
			if (M->TryGetNumberField(TEXT("SpawnRate"), Num)) Entry.SpawnRate = FMath::Max(0.1f, static_cast<float>(Num));
			if (M->TryGetNumberField(TEXT("Lifetime"), Num)) Entry.Lifetime = FMath::Max(0.05f, static_cast<float>(Num));
			if (M->TryGetNumberField(TEXT("GravityZ"), Num)) Entry.GravityZ = static_cast<float>(Num);
			int32 IntVal = 0;
			if (M->TryGetNumberField(TEXT("FacingMode"), IntVal)) Entry.FacingMode = IntVal;
			M->TryGetBoolField(TEXT("LockedAxisEnable"), Entry.bLockedAxisEnable);
			if (M->TryGetNumberField(TEXT("LockedAxisSpace"), IntVal)) Entry.LockedAxisSpace = IntVal;

			const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
			if (M->TryGetArrayField(TEXT("Scale"), Arr) && Arr && Arr->Num() >= 3)
				Entry.Scale = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("RotationEuler"), Arr) && Arr && Arr->Num() >= 3)
				Entry.RotationEuler = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("SpinSpeedEuler"), Arr) && Arr && Arr->Num() >= 3)
				Entry.SpinSpeedEuler = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("TravelVector"), Arr) && Arr && Arr->Num() >= 3)
				Entry.TravelVector = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("BoxSize"), Arr) && Arr && Arr->Num() >= 3)
				Entry.BoxSize = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("BoxCenter"), Arr) && Arr && Arr->Num() >= 3)
				Entry.BoxCenter = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("VelAvg"), Arr) && Arr && Arr->Num() >= 3)
				Entry.VelAvg = FVector3f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()));
			if (M->TryGetArrayField(TEXT("Color"), Arr) && Arr && Arr->Num() >= 4)
				Entry.Color = FLinearColor(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()), static_cast<float>((*Arr)[3]->AsNumber()));
			if (M->TryGetArrayField(TEXT("PosZRange"), Arr) && Arr && Arr->Num() >= 2)
				Entry.PosZRange = FVector2f(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()));
			if (M->TryGetNumberField(TEXT("DynamicParam3"), Num))
				Entry.DynamicParam3 = static_cast<float>(Num);

			ParseColorCurve(M, TEXT("ColorCurve"), Entry.ColorCurve);
			ParseFloatCurve(M, TEXT("ScaleCurve"), Entry.ScaleCurve);
			ParseFloatCurve(M, TEXT("ScaleRgbCurve"), Entry.ScaleRgbCurve);
			ParseFloatCurve(M, TEXT("AlphaCurve"), Entry.AlphaCurve);
			ParseFloatCurve(M, TEXT("PositionCurve"), Entry.PositionCurve);
			ParseFloatCurve(M, TEXT("DynamicParam1Curve"), Entry.DynamicParam1Curve);
			ParseFloatCurve(M, TEXT("DynamicParam2Curve"), Entry.DynamicParam2Curve);

			FString MeshJson;
			if (M->TryGetStringField(TEXT("MeshJson"), MeshJson) && !MeshJson.IsEmpty())
				Entry.Mesh = ImportStaticMeshFromJson(MeshJson);

			FString MatJson;
			if (M->TryGetStringField(TEXT("MaterialJson"), MatJson) && !MatJson.IsEmpty())
				Entry.Material = ImportMaterialFromJson(MatJson);

			ImportedMeshes.Add(Entry);
		}
	}

	TArray<FImportedLightEmitter> ImportedLights;
	const TArray<TSharedPtr<FJsonValue>>* Lights = nullptr;
	if (NObj->TryGetArrayField(TEXT("LightEmitters"), Lights) && Lights)
	{
		for (const TSharedPtr<FJsonValue>& V : *Lights)
		{
			const TSharedPtr<FJsonObject> L = V->AsObject();
			if (!L.IsValid()) continue;

			FImportedLightEmitter Entry;
			L->TryGetStringField(TEXT("EmitterName"), Entry.Name);
			L->TryGetBoolField(TEXT("Enabled"), Entry.bEnabled);
			double Num = 0.0;
			if (L->TryGetNumberField(TEXT("DefaultRadius"), Num)) Entry.Radius = static_cast<float>(Num);
			if (L->TryGetNumberField(TEXT("RadiusScale"), Num)) Entry.RadiusScale = static_cast<float>(Num);
			if (L->TryGetNumberField(TEXT("Intensity"), Num)) Entry.Intensity = static_cast<float>(Num);
			const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
			if (L->TryGetArrayField(TEXT("Color"), Arr) && Arr && Arr->Num() >= 3)
				Entry.Color = FLinearColor(static_cast<float>((*Arr)[0]->AsNumber()), static_cast<float>((*Arr)[1]->AsNumber()), static_cast<float>((*Arr)[2]->AsNumber()), 1.0f);
			ImportedLights.Add(Entry);
		}
	}

	// Create the UNiagaraSystem asset in /Game/UExplorer/Niagara and populate its emitters
	const FString RawName = NObj->GetStringField(TEXT("Name"));
	const FString AssetName = SanitizeAssetName(RawName);
	const FString NiagaraDest = TEXT("/Game/UExplorer/Niagara");
	const FString FullPath = FString::Printf(TEXT("%s/%s.%s"), *NiagaraDest, *AssetName, *AssetName);
	UNiagaraSystem* System = LoadObject<UNiagaraSystem>(nullptr, *FullPath);
	if (!System)
	{
		IAssetTools& AssetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
		UNiagaraSystemFactoryNew* NiagaraFactory = NewObject<UNiagaraSystemFactoryNew>();
		System = Cast<UNiagaraSystem>(AssetTools.CreateAsset(AssetName, NiagaraDest, UNiagaraSystem::StaticClass(), NiagaraFactory));
	}

	if (System && System->GetNumEmitters() == 0)
	{
		// Prefer Minimal template (clean emitter without Fountain cone/gravity/drag)
		UNiagaraEmitter* TemplateEmitter = LoadObject<UNiagaraEmitter>(nullptr, TEXT("/Niagara/DefaultAssets/Templates/Emitters/Minimal.Minimal"));
		if (!TemplateEmitter)
			TemplateEmitter = LoadObject<UNiagaraEmitter>(nullptr, TEXT("/Niagara/DefaultAssets/Templates/Emitters/SimpleSpriteBurst.SimpleSpriteBurst"));
		if (!TemplateEmitter)
			TemplateEmitter = LoadObject<UNiagaraEmitter>(nullptr, TEXT("/Niagara/DefaultAssets/Templates/Emitters/Fountain.Fountain"));

		if (TemplateEmitter)
		{
			const FGuid TemplateVersion = TemplateEmitter->GetExposedVersion().VersionGuid;
			UNiagaraScript* FloatCurveDI = LoadObject<UNiagaraScript>(nullptr, TEXT("/Niagara/DynamicInputs/Curves/FloatFromCurve.FloatFromCurve"));
			UNiagaraScript* ColorCurveDI = LoadObject<UNiagaraScript>(nullptr, TEXT("/Niagara/DynamicInputs/Curves/ColorFromCurve.ColorFromCurve"));

			auto FindExistingModule = [](FVersionedNiagaraEmitterData* EmitterData, const TCHAR* NameContains) -> UNiagaraNodeFunctionCall*
			{
				if (!EmitterData) return nullptr;
				UNiagaraScriptSource* Source = Cast<UNiagaraScriptSource>(EmitterData->GraphSource);
				if (!Source || !Source->NodeGraph) return nullptr;
				const FString TargetNoSpace = FString(NameContains).Replace(TEXT(" "), TEXT(""));
				for (UEdGraphNode* Node : Source->NodeGraph->Nodes)
				{
					if (UNiagaraNodeFunctionCall* Fn = Cast<UNiagaraNodeFunctionCall>(Node))
					{
						const FString FnNameNoSpace = Fn->GetFunctionName().Replace(TEXT(" "), TEXT(""));
						if (FnNameNoSpace.Contains(TargetNoSpace) || (Fn->FunctionScript && Fn->FunctionScript->GetName().Contains(TargetNoSpace)))
							return Fn;
					}
				}
				return nullptr;
			};

			auto LoadNiagaraScriptByPathOrName = [](const TCHAR* ModuleAssetPath) -> UNiagaraScript*
			{
				if (UNiagaraScript* Direct = LoadObject<UNiagaraScript>(nullptr, ModuleAssetPath))
					return Direct;
				FString PathStr(ModuleAssetPath);
				int32 DotIdx = INDEX_NONE;
				if (PathStr.FindLastChar(TEXT('.'), DotIdx))
					PathStr = PathStr.Mid(DotIdx + 1);
				FAssetRegistryModule& AssetRegistryModule = FModuleManager::LoadModuleChecked<FAssetRegistryModule>("AssetRegistry");
				TArray<FAssetData> Assets;
				FARFilter Filter;
				Filter.PackagePaths.Add(TEXT("/Niagara"));
				Filter.bRecursivePaths = true;
				Filter.ClassPaths.Add(UNiagaraScript::StaticClass()->GetClassPathName());
				AssetRegistryModule.Get().GetAssets(Filter, Assets);
				for (const FAssetData& Asset : Assets)
				{
					if (Asset.AssetName.ToString().Equals(PathStr, ESearchCase::IgnoreCase))
					{
						if (UNiagaraScript* Loaded = Cast<UNiagaraScript>(Asset.GetAsset()))
							return Loaded;
					}
				}
				UE_LOG(LogTemp, Warning, TEXT("[UExplorerBridge] Failed to load Niagara module script: %s"), ModuleAssetPath);
				return nullptr;
			};

			auto AddNiagaraModule = [&LoadNiagaraScriptByPathOrName](FVersionedNiagaraEmitterData* EmitterData, ENiagaraScriptUsage Usage, const TCHAR* ModuleAssetPath) -> UNiagaraNodeFunctionCall*
			{
				if (!EmitterData) return nullptr;
				UNiagaraScriptSource* Source = Cast<UNiagaraScriptSource>(EmitterData->GraphSource);
				if (!Source || !Source->NodeGraph) return nullptr;
				UNiagaraNodeOutput* OutputNode = Source->NodeGraph->FindEquivalentOutputNode(Usage, FGuid());
				if (!OutputNode) return nullptr;
				if (UNiagaraScript* ModScript = LoadNiagaraScriptByPathOrName(ModuleAssetPath))
				{
					return FNiagaraStackGraphUtilities::AddScriptModuleToStack(ModScript, *OutputNode);
				}
				return nullptr;
			};

			auto SetPinDefaultFromVar = [](UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const FNiagaraTypeDefinition& TypeDef, const FNiagaraVariable& VarWithData)
			{
				if (!ModNode) return;
				const FNiagaraParameterHandle Handle(FName(*ModNode->GetFunctionName()), FName(InputName));
				UEdGraphPin& OverridePin = FNiagaraStackGraphUtilities::GetOrCreateStackFunctionInputOverridePin(
					*ModNode, Handle, TypeDef, FGuid(), FGuid());
				FString PinDefaultValue;
				if (GetDefault<UEdGraphSchema_Niagara>()->TryGetPinDefaultValueFromNiagaraVariable(VarWithData, PinDefaultValue))
				{
					OverridePin.Modify();
					OverridePin.DefaultValue = PinDefaultValue;
					if (UNiagaraNode* OwningNode = Cast<UNiagaraNode>(OverridePin.GetOwningNode()))
						OwningNode->MarkNodeRequiresSynchronization(TEXT("UExplorerOverride"), true);
				}
			};

			auto SetStaticSwitchEnum = [](UNiagaraNodeFunctionCall* ModNode, const TCHAR* SwitchName, const TCHAR* SubstringMatch)
			{
				if (!ModNode) return;
				for (int32 Pass = 0; Pass < 2; ++Pass)
				{
					for (UEdGraphPin* Pin : ModNode->Pins)
					{
						if (!Pin || Pin->Direction != EGPD_Input) continue;
						const FString PinStr = Pin->PinName.ToString();
						const bool bMatch = (Pass == 0)
							? PinStr.Equals(SwitchName, ESearchCase::IgnoreCase)
							: PinStr.Contains(SwitchName, ESearchCase::IgnoreCase);
						if (!bMatch) continue;
						if (UEnum* EnumObj = Cast<UEnum>(Pin->PinType.PinSubCategoryObject.Get()))
						{
							for (int32 idx = 0; idx < EnumObj->NumEnums() - 1; ++idx)
							{
								const FString Disp = EnumObj->GetDisplayNameTextByIndex(idx).ToString();
								const FString NameStr = EnumObj->GetNameStringByIndex(idx);
								if (Disp.Contains(SubstringMatch, ESearchCase::IgnoreCase) || NameStr.Contains(SubstringMatch, ESearchCase::IgnoreCase))
								{
									Pin->Modify();
									Pin->DefaultValue = NameStr;
									ModNode->MarkNodeRequiresSynchronization(TEXT("UExplorerStaticSwitch"), true);
									return;
								}
							}
						}
					}
				}
			};

			auto SetStaticSwitchBool = [](UNiagaraNodeFunctionCall* ModNode, const TCHAR* SwitchName, bool bValue)
			{
				if (!ModNode) return;
				for (UEdGraphPin* Pin : ModNode->Pins)
				{
					if (!Pin || Pin->Direction != EGPD_Input) continue;
					if (!Pin->PinName.ToString().Equals(SwitchName, ESearchCase::IgnoreCase)) continue;
					Pin->Modify();
					Pin->DefaultValue = bValue ? TEXT("true") : TEXT("false");
					ModNode->MarkNodeRequiresSynchronization(TEXT("UExplorerStaticSwitchBool"), true);
					return;
				}
			};

			auto MakeRapidVar = [](const FString& UniqueName, ENiagaraScriptUsage Usage, const FName& Aliased, const FNiagaraTypeDefinition& TypeDef)
			{
				const bool bSysScript = (Usage == ENiagaraScriptUsage::SystemSpawnScript || Usage == ENiagaraScriptUsage::SystemUpdateScript);
				return FNiagaraUtilities::ConvertVariableToRapidIterationConstantName(
					FNiagaraVariable(TypeDef, Aliased), bSysScript ? nullptr : *UniqueName, Usage);
			};

			auto SetRapidFloat = [&SetPinDefaultFromVar, &MakeRapidVar](FVersionedNiagaraEmitterData* EmitterData, const FString& UniqueName, ENiagaraScriptUsage Usage, UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, float Value)
			{
				if (!EmitterData || !ModNode) return;
				FNiagaraVariable LocalVar(FNiagaraTypeDefinition::GetFloatDef(), NAME_None);
				LocalVar.SetValue<float>(Value);
				SetPinDefaultFromVar(ModNode, InputName, FNiagaraTypeDefinition::GetFloatDef(), LocalVar);
				if (UNiagaraScript* Script = EmitterData->GetScript(Usage, FGuid()))
				{
					const FName Aliased(*FString::Printf(TEXT("%s.%s"), *ModNode->GetFunctionName(), InputName));
					FNiagaraVariable Var = MakeRapidVar(UniqueName, Usage, Aliased, FNiagaraTypeDefinition::GetFloatDef());
					Script->RapidIterationParameters.SetParameterValue(Value, Var, true);
				}
			};

			auto SetRapidVec2 = [&SetPinDefaultFromVar, &MakeRapidVar](FVersionedNiagaraEmitterData* EmitterData, const FString& UniqueName, ENiagaraScriptUsage Usage, UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const FVector2f& Value)
			{
				if (!EmitterData || !ModNode) return;
				FNiagaraVariable LocalVar(FNiagaraTypeDefinition::GetVec2Def(), NAME_None);
				LocalVar.SetValue<FVector2f>(Value);
				SetPinDefaultFromVar(ModNode, InputName, FNiagaraTypeDefinition::GetVec2Def(), LocalVar);
				if (UNiagaraScript* Script = EmitterData->GetScript(Usage, FGuid()))
				{
					const FName Aliased(*FString::Printf(TEXT("%s.%s"), *ModNode->GetFunctionName(), InputName));
					FNiagaraVariable Var = MakeRapidVar(UniqueName, Usage, Aliased, FNiagaraTypeDefinition::GetVec2Def());
					Script->RapidIterationParameters.SetParameterValue(Value, Var, true);
				}
			};

			auto SetRapidVec3 = [&SetPinDefaultFromVar, &MakeRapidVar](FVersionedNiagaraEmitterData* EmitterData, const FString& UniqueName, ENiagaraScriptUsage Usage, UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const FVector3f& Value)
			{
				if (!EmitterData || !ModNode) return;
				FNiagaraVariable LocalVar(FNiagaraTypeDefinition::GetVec3Def(), NAME_None);
				LocalVar.SetValue<FVector3f>(Value);
				SetPinDefaultFromVar(ModNode, InputName, FNiagaraTypeDefinition::GetVec3Def(), LocalVar);
				if (UNiagaraScript* Script = EmitterData->GetScript(Usage, FGuid()))
				{
					const FName Aliased(*FString::Printf(TEXT("%s.%s"), *ModNode->GetFunctionName(), InputName));
					FNiagaraVariable Var = MakeRapidVar(UniqueName, Usage, Aliased, FNiagaraTypeDefinition::GetVec3Def());
					Script->RapidIterationParameters.SetParameterValue(Value, Var, true);
				}
			};

			auto SetRapidColor = [&SetPinDefaultFromVar, &MakeRapidVar](FVersionedNiagaraEmitterData* EmitterData, const FString& UniqueName, ENiagaraScriptUsage Usage, UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const FLinearColor& Value)
			{
				if (!EmitterData || !ModNode) return;
				FNiagaraVariable LocalVar(FNiagaraTypeDefinition::GetColorDef(), NAME_None);
				LocalVar.SetValue<FLinearColor>(Value);
				SetPinDefaultFromVar(ModNode, InputName, FNiagaraTypeDefinition::GetColorDef(), LocalVar);
				if (UNiagaraScript* Script = EmitterData->GetScript(Usage, FGuid()))
				{
					const FName Aliased(*FString::Printf(TEXT("%s.%s"), *ModNode->GetFunctionName(), InputName));
					FNiagaraVariable Var = MakeRapidVar(UniqueName, Usage, Aliased, FNiagaraTypeDefinition::GetColorDef());
					Script->RapidIterationParameters.SetParameterValue(Value, Var, true);
				}
			};

			auto SetRapidQuat = [&SetPinDefaultFromVar, &MakeRapidVar](FVersionedNiagaraEmitterData* EmitterData, const FString& UniqueName, ENiagaraScriptUsage Usage, UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const FQuat4f& Value)
			{
				if (!EmitterData || !ModNode) return;
				FNiagaraVariable LocalVar(FNiagaraTypeDefinition::GetQuatDef(), NAME_None);
				LocalVar.SetValue<FQuat4f>(Value);
				SetPinDefaultFromVar(ModNode, InputName, FNiagaraTypeDefinition::GetQuatDef(), LocalVar);
				if (UNiagaraScript* Script = EmitterData->GetScript(Usage, FGuid()))
				{
					const FName Aliased(*FString::Printf(TEXT("%s.%s"), *ModNode->GetFunctionName(), InputName));
					FNiagaraVariable Var = MakeRapidVar(UniqueName, Usage, Aliased, FNiagaraTypeDefinition::GetQuatDef());
					Script->RapidIterationParameters.SetParameterValue(Value, Var, true);
				}
			};

			auto SetFloatCurveInput = [&](UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const TArray<FFloatCurveKey>& Keys)
			{
				if (!ModNode || !FloatCurveDI || Keys.Num() == 0) return;
				const FNiagaraParameterHandle Handle(FName(*ModNode->GetFunctionName()), FName(InputName));
				UEdGraphPin& OverridePin = FNiagaraStackGraphUtilities::GetOrCreateStackFunctionInputOverridePin(
					*ModNode, Handle, FNiagaraTypeDefinition::GetFloatDef(), FGuid(), FGuid());
				UNiagaraNodeFunctionCall* DynNode = nullptr;
				FNiagaraStackGraphUtilities::SetDynamicInputForFunctionInput(OverridePin, FloatCurveDI, DynNode);
				if (DynNode)
				{
					const FNiagaraParameterHandle CurveHandle(FName(*DynNode->GetFunctionName()), FName(TEXT("Curve")));
					UEdGraphPin& CurvePin = FNiagaraStackGraphUtilities::GetOrCreateStackFunctionInputOverridePin(
						*DynNode, CurveHandle, FNiagaraTypeDefinition(UNiagaraDataInterfaceCurve::StaticClass()), FGuid(), FGuid());
					UNiagaraDataInterface* OutDI = nullptr;
					FNiagaraStackGraphUtilities::SetDataInterfaceValueForFunctionInput(CurvePin, UNiagaraDataInterfaceCurve::StaticClass(), TEXT("Curve"), OutDI);
					if (UNiagaraDataInterfaceCurve* CurveObj = Cast<UNiagaraDataInterfaceCurve>(OutDI))
					{
						CurveObj->Curve.Reset();
						for (const FFloatCurveKey& K : Keys)
							CurveObj->Curve.AddKey(K.Time, K.Value);
						CurveObj->UpdateLUT();
					}
				}
			};

			auto SetColorCurveInput = [&](UNiagaraNodeFunctionCall* ModNode, const TCHAR* InputName, const TArray<FColorCurveKey>& Keys)
			{
				if (!ModNode || !ColorCurveDI || Keys.Num() == 0) return;
				const FNiagaraParameterHandle Handle(FName(*ModNode->GetFunctionName()), FName(InputName));
				UEdGraphPin& OverridePin = FNiagaraStackGraphUtilities::GetOrCreateStackFunctionInputOverridePin(
					*ModNode, Handle, FNiagaraTypeDefinition::GetColorDef(), FGuid(), FGuid());
				UNiagaraNodeFunctionCall* DynNode = nullptr;
				FNiagaraStackGraphUtilities::SetDynamicInputForFunctionInput(OverridePin, ColorCurveDI, DynNode);
				if (DynNode)
				{
					const FNiagaraParameterHandle CurveHandle(FName(*DynNode->GetFunctionName()), FName(TEXT("ColorCurve")));
					UEdGraphPin& CurvePin = FNiagaraStackGraphUtilities::GetOrCreateStackFunctionInputOverridePin(
						*DynNode, CurveHandle, FNiagaraTypeDefinition(UNiagaraDataInterfaceColorCurve::StaticClass()), FGuid(), FGuid());
					UNiagaraDataInterface* OutDI = nullptr;
					FNiagaraStackGraphUtilities::SetDataInterfaceValueForFunctionInput(CurvePin, UNiagaraDataInterfaceColorCurve::StaticClass(), TEXT("ColorCurve"), OutDI);
					if (UNiagaraDataInterfaceColorCurve* CurveObj = Cast<UNiagaraDataInterfaceColorCurve>(OutDI))
					{
						CurveObj->RedCurve.Reset();
						CurveObj->GreenCurve.Reset();
						CurveObj->BlueCurve.Reset();
						CurveObj->AlphaCurve.Reset();
						for (const FColorCurveKey& K : Keys)
						{
							CurveObj->RedCurve.AddKey(K.Time, K.Color.R);
							CurveObj->GreenCurve.AddKey(K.Time, K.Color.G);
							CurveObj->BlueCurve.AddKey(K.Time, K.Color.B);
							CurveObj->AlphaCurve.AddKey(K.Time, K.Color.A);
						}
						CurveObj->UpdateLUT();
					}
				}
			};

			for (const FImportedSpriteEmitter& SpriteInfo : ImportedSprites)
			{
				FGuid HandleId = FNiagaraEditorUtilities::AddEmitterToSystem(*System, *TemplateEmitter, TemplateVersion);
				for (int32 i = 0; i < System->GetNumEmitters(); ++i)
				{
					FNiagaraEmitterHandle& Handle = System->GetEmitterHandle(i);
					if (Handle.GetId() != HandleId) continue;
					if (!SpriteInfo.Name.IsEmpty())
						Handle.SetName(FName(*SpriteInfo.Name), *System);
					Handle.SetIsEnabled(SpriteInfo.bEnabled, *System, false);
					const FString UniqueName = Handle.GetUniqueInstanceName();
					if (FVersionedNiagaraEmitterData* EmitterData = Handle.GetEmitterData())
					{
						EmitterData->bLocalSpace = SpriteInfo.bLocalSpace;

						if (UNiagaraNodeFunctionCall* StateMod = FindExistingModule(EmitterData, TEXT("EmitterState")))
						{
							SetStaticSwitchEnum(StateMod, TEXT("Life Cycle Mode"), TEXT("Self"));
							SetStaticSwitchEnum(StateMod, TEXT("Loop Behavior"), TEXT("Infinite"));
						}

						if (UNiagaraNodeFunctionCall* SpawnMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::EmitterUpdateScript, TEXT("/Niagara/Modules/Emitter/SpawnRate.SpawnRate")))
							SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::EmitterUpdateScript, SpawnMod, TEXT("SpawnRate"), SpriteInfo.SpawnRate);

						UNiagaraNodeFunctionCall* InitMod = FindExistingModule(EmitterData, TEXT("InitializeParticle"));
						if (!InitMod)
							InitMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Initialization/V2/InitializeParticle.InitializeParticle"));
						if (InitMod)
						{
							SetStaticSwitchEnum(InitMod, TEXT("Lifetime Mode"), TEXT("Direct"));
							SetStaticSwitchEnum(InitMod, TEXT("Color Mode"), TEXT("Direct"));
							SetStaticSwitchEnum(InitMod, TEXT("Sprite Size Mode"), TEXT("Non"));
							SetStaticSwitchEnum(InitMod, TEXT("Sprite Rotation Mode"), TEXT("Direct Angle"));
							SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Lifetime"), SpriteInfo.Lifetime);
							SetRapidVec2(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Sprite Size"), SpriteInfo.SpriteSize);
							SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Sprite Rotation Angle"), SpriteInfo.SpriteRotation);
							SetRapidColor(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Color"), SpriteInfo.Color);
						}

						if (SpriteInfo.SpawnRadius > 1.0f)
						{
							if (UNiagaraNodeFunctionCall* ShapeMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Location/V2/ShapeLocation.ShapeLocation")))
							{
								if (SpriteInfo.VerticalLineLength > 1.0f)
								{
									SetStaticSwitchEnum(ShapeMod, TEXT("Shape Primitive"), TEXT("Cylinder"));
									SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, ShapeMod, TEXT("Cylinder Radius"), SpriteInfo.SpawnRadius);
									SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, ShapeMod, TEXT("Cylinder Height"), SpriteInfo.VerticalLineLength);
								}
								else
								{
									SetStaticSwitchEnum(ShapeMod, TEXT("Shape Primitive"), TEXT("Sphere"));
									SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, ShapeMod, TEXT("Sphere Radius"), SpriteInfo.SpawnRadius);
								}
								if (!SpriteInfo.OriginOffset.IsNearlyZero())
									SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, ShapeMod, TEXT("Shape Origin"), SpriteInfo.OriginOffset);
							}
						}
						else if (SpriteInfo.BoxSize.GetMax() > 1.0f)
						{
							if (UNiagaraNodeFunctionCall* BoxMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Location/BoxLocation.BoxLocation")))
							{
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, BoxMod, TEXT("Box Size"), SpriteInfo.BoxSize);
								if (!SpriteInfo.BoxCenter.IsNearlyZero())
									SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, BoxMod, TEXT("Box Origin"), SpriteInfo.BoxCenter);
							}
						}

						const FVector3f EffVel = !FMath::IsNearlyZero(SpriteInfo.VelocitySpeed)
							? FVector3f(0.0f, 0.0f, SpriteInfo.VelocitySpeed)
							: SpriteInfo.VelAvg;
						if (!EffVel.IsNearlyZero())
						{
							if (UNiagaraNodeFunctionCall* VelMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Velocity/AddVelocity.AddVelocity")))
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, VelMod, TEXT("Velocity"), EffVel);
						}
						if (!FMath::IsNearlyZero(SpriteInfo.GravityZ))
						{
							if (UNiagaraNodeFunctionCall* GravMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Forces/GravityForce.GravityForce")))
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, GravMod, TEXT("Gravity"), FVector3f(0.0f, 0.0f, SpriteInfo.GravityZ));
						}
						if (SpriteInfo.TurbulenceStrength > 0.01f)
						{
							if (UNiagaraNodeFunctionCall* CurlMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Forces/V2/CurlNoiseForce.CurlNoiseForce")))
								SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, CurlMod, TEXT("Noise Strength"), SpriteInfo.TurbulenceStrength * 100.0f);
						}
						if (!EffVel.IsNearlyZero() || !FMath::IsNearlyZero(SpriteInfo.GravityZ) || SpriteInfo.TurbulenceStrength > 0.01f)
							AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Solvers/SolveForcesAndVelocity.SolveForcesAndVelocity"));

						if (UNiagaraNodeFunctionCall* ScaleColorMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Color/ScaleColor.ScaleColor")))
						{
							if (SpriteInfo.AlphaCurve.Num() > 0)
								SetFloatCurveInput(ScaleColorMod, TEXT("Scale Alpha"), SpriteInfo.AlphaCurve);
						}
						if (SpriteInfo.ColorCurve.Num() > 0)
						{
							if (UNiagaraNodeFunctionCall* ColorMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Color/Color.Color")))
								SetColorCurveInput(ColorMod, TEXT("Color"), SpriteInfo.ColorCurve);
						}
						if (SpriteInfo.ScaleCurve.Num() > 0)
						{
							if (UNiagaraNodeFunctionCall* ScaleSizeMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Size/ScaleSpriteSize.ScaleSpriteSize")))
							{
								SetStaticSwitchEnum(ScaleSizeMod, TEXT("Scale Sprite Size Mode"), TEXT("Uniform"));
								SetFloatCurveInput(ScaleSizeMod, TEXT("Uniform Scale Factor"), SpriteInfo.ScaleCurve);
							}
						}

						for (UNiagaraRendererProperties* Renderer : EmitterData->GetRenderers())
						{
							if (UNiagaraSpriteRendererProperties* SpriteRenderer = Cast<UNiagaraSpriteRendererProperties>(Renderer))
							{
								if (SpriteInfo.Material)
								{
									if (UMaterial* BaseMat = SpriteInfo.Material->GetMaterial())
									{
										if (!BaseMat->bUsedWithNiagaraSprites)
										{
											BaseMat->bUsedWithNiagaraSprites = true;
											BaseMat->PostEditChange();
										}
									}
									SpriteRenderer->Material = SpriteInfo.Material;
								}
								SpriteRenderer->SubImageSize = SpriteInfo.SubImageSize;
								SpriteRenderer->PivotInUVSpace = SpriteInfo.PivotInUVSpace;
								SpriteRenderer->bSubImageBlend = SpriteInfo.bSubImageBlend;
								SpriteRenderer->PostEditChange();
							}
						}

						// Attach LightRenderer if this emitter also drives a Niagara light (e.g. GlowLight)
						for (const FImportedLightEmitter& LightInfo : ImportedLights)
						{
							if (LightInfo.Name.Equals(SpriteInfo.Name, ESearchCase::IgnoreCase))
							{
								UNiagaraLightRendererProperties* LightRenderer = NewObject<UNiagaraLightRendererProperties>(Handle.GetInstance().Emitter);
								LightRenderer->RadiusScale = FMath::Max(1.0f, LightInfo.Radius / 100.0f);
								Handle.GetInstance().Emitter->AddRenderer(LightRenderer, Handle.GetInstance().Version);
								break;
							}
						}
					}
				}
			}

			for (const FImportedMeshEmitter& MeshInfo : ImportedMeshes)
			{
				FGuid HandleId = FNiagaraEditorUtilities::AddEmitterToSystem(*System, *TemplateEmitter, TemplateVersion);
				for (int32 i = 0; i < System->GetNumEmitters(); ++i)
				{
					FNiagaraEmitterHandle& Handle = System->GetEmitterHandle(i);
					if (Handle.GetId() != HandleId) continue;
					if (!MeshInfo.Name.IsEmpty())
						Handle.SetName(FName(*MeshInfo.Name), *System);
					Handle.SetIsEnabled(MeshInfo.bEnabled, *System, false);
					const FString UniqueName = Handle.GetUniqueInstanceName();
					if (FVersionedNiagaraEmitterData* EmitterData = Handle.GetEmitterData())
					{
						EmitterData->bLocalSpace = MeshInfo.bLocalSpace;

						if (UNiagaraNodeFunctionCall* StateMod = FindExistingModule(EmitterData, TEXT("EmitterState")))
						{
							SetStaticSwitchEnum(StateMod, TEXT("Life Cycle Mode"), TEXT("Self"));
							SetStaticSwitchEnum(StateMod, TEXT("Loop Behavior"), TEXT("Infinite"));
						}

						if (UNiagaraNodeFunctionCall* SpawnMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::EmitterUpdateScript, TEXT("/Niagara/Modules/Emitter/SpawnRate.SpawnRate")))
							SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::EmitterUpdateScript, SpawnMod, TEXT("SpawnRate"), MeshInfo.SpawnRate);

						UNiagaraNodeFunctionCall* InitMod = FindExistingModule(EmitterData, TEXT("InitializeParticle"));
						if (!InitMod)
							InitMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Initialization/V2/InitializeParticle.InitializeParticle"));
						if (InitMod)
						{
							SetStaticSwitchEnum(InitMod, TEXT("Lifetime Mode"), TEXT("Direct"));
							SetStaticSwitchEnum(InitMod, TEXT("Color Mode"), TEXT("Direct"));
							SetStaticSwitchEnum(InitMod, TEXT("Mesh Scale Mode"), TEXT("Non"));
							SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Lifetime"), MeshInfo.Lifetime);
							SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Mesh Scale"), MeshInfo.Scale);
							SetRapidColor(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitMod, TEXT("Color"), MeshInfo.Color);
						}

						const float ZSpan = MeshInfo.PosZRange.Y - MeshInfo.PosZRange.X;
						const FVector3f EffBoxSize = (MeshInfo.BoxSize.GetMax() > 0.1f)
							? MeshInfo.BoxSize
							: (ZSpan > 0.1f ? FVector3f(0.0f, 0.0f, ZSpan) : FVector3f::ZeroVector);
						if (EffBoxSize.GetMax() > 0.1f)
						{
							if (UNiagaraNodeFunctionCall* BoxMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Location/BoxLocation.BoxLocation")))
							{
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, BoxMod, TEXT("Box Size"), EffBoxSize);
								if (!MeshInfo.BoxCenter.IsNearlyZero())
									SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, BoxMod, TEXT("Box Origin"), MeshInfo.BoxCenter);
							}
						}

						if (!MeshInfo.RotationEuler.IsNearlyZero())
						{
							if (UNiagaraNodeFunctionCall* InitOrient = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Orientation/V3/InitialMeshOrientation.InitialMeshOrientation")))
							{
								SetStaticSwitchEnum(InitOrient, TEXT("Mesh Orientation Mode"), TEXT("Orient to Quaternion"));
								const FQuat4f Q(FRotator(MeshInfo.RotationEuler.X, MeshInfo.RotationEuler.Y, MeshInfo.RotationEuler.Z).Quaternion());
								SetRapidQuat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, InitOrient, TEXT("Orientation Quaternion"), Q);
							}
						}

						const FVector3f MeshVel = !MeshInfo.TravelVector.IsNearlyZero()
							? (MeshInfo.TravelVector / FMath::Max(0.1f, MeshInfo.Lifetime))
							: MeshInfo.VelAvg;
						if (!MeshVel.IsNearlyZero())
						{
							if (UNiagaraNodeFunctionCall* VelMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleSpawnScript, TEXT("/Niagara/Modules/Spawn/Velocity/AddVelocity.AddVelocity")))
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleSpawnScript, VelMod, TEXT("Velocity"), MeshVel);
						}
						if (!FMath::IsNearlyZero(MeshInfo.GravityZ))
						{
							if (UNiagaraNodeFunctionCall* GravMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Forces/GravityForce.GravityForce")))
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, GravMod, TEXT("Gravity"), FVector3f(0.0f, 0.0f, MeshInfo.GravityZ));
						}
						if (!MeshVel.IsNearlyZero() || !FMath::IsNearlyZero(MeshInfo.GravityZ))
							AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Solvers/SolveForcesAndVelocity.SolveForcesAndVelocity"));

						if (!MeshInfo.SpinSpeedEuler.IsNearlyZero())
						{
							if (UNiagaraNodeFunctionCall* UpdOrient = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Orientation/V3/UpdateMeshOrientation.UpdateMeshOrientation")))
							{
								SetStaticSwitchEnum(UpdOrient, TEXT("Orientation"), TEXT("Rotation Rate"));
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, UpdOrient, TEXT("Rotation Rate"), MeshInfo.SpinSpeedEuler);
							}
						}

						if (UNiagaraNodeFunctionCall* ScaleColorMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Color/ScaleColor.ScaleColor")))
						{
							if (MeshInfo.AlphaCurve.Num() > 0)
								SetFloatCurveInput(ScaleColorMod, TEXT("Scale Alpha"), MeshInfo.AlphaCurve);
						}
						if (MeshInfo.ColorCurve.Num() > 0)
						{
							if (UNiagaraNodeFunctionCall* ColorMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Color/Color.Color")))
								SetColorCurveInput(ColorMod, TEXT("Color"), MeshInfo.ColorCurve);
						}
						if (MeshInfo.ScaleCurve.Num() > 0)
						{
							TArray<FFloatCurveKey> ScaledKeys = MeshInfo.ScaleCurve;
							const float BaseScale = FMath::Max(0.01f, MeshInfo.Scale.X);
							if (!FMath::IsNearlyEqual(BaseScale, 1.0f))
							{
								for (FFloatCurveKey& K : ScaledKeys) K.Value *= BaseScale;
							}
							if (UNiagaraNodeFunctionCall* ScaleMeshMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Size/ScaleMeshSize.ScaleMeshSize")))
								SetFloatCurveInput(ScaleMeshMod, TEXT("Scale Factor"), ScaledKeys);
						}
						else if (!MeshInfo.Scale.Equals(FVector3f::OneVector))
						{
							if (UNiagaraNodeFunctionCall* ScaleMeshMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Size/ScaleMeshSize.ScaleMeshSize")))
								SetRapidVec3(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, ScaleMeshMod, TEXT("Scale Factor"), MeshInfo.Scale);
						}

						if (UNiagaraNodeFunctionCall* DynMatMod = AddNiagaraModule(EmitterData, ENiagaraScriptUsage::ParticleUpdateScript, TEXT("/Niagara/Modules/Update/Material/DynamicMaterialParameters.DynamicMaterialParameters")))
						{
							if (MeshInfo.DynamicParam1Curve.Num() > 0)
							{
								SetStaticSwitchBool(DynMatMod, TEXT("Param 1 Enabled"), true);
								SetFloatCurveInput(DynMatMod, TEXT("Param 1"), MeshInfo.DynamicParam1Curve);
							}
							if (MeshInfo.DynamicParam2Curve.Num() > 0)
							{
								SetStaticSwitchBool(DynMatMod, TEXT("Param 2 Enabled"), true);
								SetFloatCurveInput(DynMatMod, TEXT("Param 2"), MeshInfo.DynamicParam2Curve);
							}
							if (!FMath::IsNearlyZero(MeshInfo.DynamicParam3))
							{
								SetStaticSwitchBool(DynMatMod, TEXT("Param 3 Enabled"), true);
								SetRapidFloat(EmitterData, UniqueName, ENiagaraScriptUsage::ParticleUpdateScript, DynMatMod, TEXT("Param 3"), MeshInfo.DynamicParam3);
							}
						}

						// Disable default sprite renderer and add a mesh renderer
						for (UNiagaraRendererProperties* Renderer : EmitterData->GetRenderers())
						{
							Renderer->SetIsEnabled(false);
						}

						UNiagaraMeshRendererProperties* MeshRenderer = NewObject<UNiagaraMeshRendererProperties>(Handle.GetInstance().Emitter, NAME_None, RF_Transactional);
						MeshRenderer->FacingMode = static_cast<ENiagaraMeshFacingMode>(FMath::Clamp(MeshInfo.FacingMode, 0, 3));
						MeshRenderer->bLockedAxisEnable = MeshInfo.bLockedAxisEnable;
						MeshRenderer->LockedAxis = FVector(MeshInfo.LockedAxis);
						MeshRenderer->LockedAxisSpace = static_cast<ENiagaraMeshLockedAxisSpace>(FMath::Clamp(MeshInfo.LockedAxisSpace, 0, 2));
						if (MeshInfo.Mesh)
						{
							FNiagaraMeshRendererMeshProperties MeshProp;
							MeshProp.Mesh = MeshInfo.Mesh;
							MeshRenderer->Meshes.Empty();
							MeshRenderer->Meshes.Add(MeshProp);
						}
						if (MeshInfo.Material)
						{
							if (UMaterial* BaseMat = MeshInfo.Material->GetMaterial())
							{
								if (!BaseMat->bUsedWithNiagaraMeshParticles)
								{
									BaseMat->bUsedWithNiagaraMeshParticles = true;
									BaseMat->PostEditChange();
								}
							}
							MeshRenderer->bOverrideMaterials = true;
							FNiagaraMeshMaterialOverride OverrideMat;
							OverrideMat.ExplicitMat = MeshInfo.Material;
							MeshRenderer->OverrideMaterials.Add(OverrideMat);
						}
						MeshRenderer->PostEditChange();
						Handle.GetInstance().Emitter->AddRenderer(MeshRenderer, Handle.GetInstance().Version);
					}
				}
			}
		}

		System->RequestCompile(false);
		System->MarkPackageDirty();
		UE_LOG(LogUExplorerImporter, Log, TEXT("Populated UNiagaraSystem '%s' (%d sprite, %d mesh emitters)"),
			*AssetName, ImportedSprites.Num(), ImportedMeshes.Num());
	}

	return true;
}

bool FUExplorerAssetImporter::ImportWorldFromJson(const FString& WorldJsonPath)
{
	TSharedPtr<FJsonObject> WObj = LoadJsonObjectFromFile(WorldJsonPath);
	if (!WObj.IsValid() || !GEditor)
	{
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* Actors = nullptr;
	if (!WObj->TryGetArrayField(TEXT("Actors"), Actors) || !Actors)
	{
		return false;
	}

	FString RawWorldName;
	if (!WObj->TryGetStringField(TEXT("Name"), RawWorldName) || RawWorldName.IsEmpty())
	{
		RawWorldName = FPaths::GetBaseFilename(FPaths::GetPath(WorldJsonPath));
	}
	const FString SafeWorldName = SanitizeAssetName(RawWorldName.IsEmpty() ? TEXT("ImportedWorld") : RawWorldName);
	const FString MapAssetPath = FString::Printf(TEXT("/Game/UExplorer/Maps/%s"), *SafeWorldName);

	UWorld* EditorWorld = UEditorLoadingAndSavingUtils::NewBlankMap(false);
	if (!EditorWorld)
	{
		EditorWorld = GEditor->GetEditorWorldContext().World();
	}
	if (!EditorWorld)
	{
		return false;
	}

	auto ParseTransformFromJson = [](const TSharedPtr<FJsonObject>& Obj, FVector& OutLoc, FRotator& OutRot, FVector& OutScale)
	{
		OutLoc = FVector::ZeroVector;
		OutRot = FRotator::ZeroRotator;
		OutScale = FVector::OneVector;
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (Obj->TryGetArrayField(TEXT("LocationCm"), Arr) && Arr && Arr->Num() >= 3)
			OutLoc = FVector((*Arr)[0]->AsNumber(), (*Arr)[1]->AsNumber(), (*Arr)[2]->AsNumber());
		if (Obj->TryGetArrayField(TEXT("RotationDeg"), Arr) && Arr && Arr->Num() >= 3)
			OutRot = FRotator((*Arr)[0]->AsNumber(), (*Arr)[1]->AsNumber(), (*Arr)[2]->AsNumber());
		if (Obj->TryGetArrayField(TEXT("Scale"), Arr) && Arr && Arr->Num() >= 3)
			OutScale = FVector((*Arr)[0]->AsNumber(), (*Arr)[1]->AsNumber(), (*Arr)[2]->AsNumber());
	};

	auto ApplyLightColor = [](ULightComponentBase* LightComp, const TSharedPtr<FJsonObject>& Obj)
	{
		if (!LightComp) return;
		const TArray<TSharedPtr<FJsonValue>>* ColArr = nullptr;
		if (Obj->TryGetArrayField(TEXT("Color"), ColArr) && ColArr && ColArr->Num() >= 3)
		{
			LightComp->LightColor = FLinearColor(
				static_cast<float>((*ColArr)[0]->AsNumber()),
				static_cast<float>((*ColArr)[1]->AsNumber()),
				static_cast<float>((*ColArr)[2]->AsNumber()),
				1.0f).ToFColor(true);
		}
	};

	for (const TSharedPtr<FJsonValue>& Val : *Actors)
	{
		const TSharedPtr<FJsonObject> AObj = Val->AsObject();
		if (!AObj.IsValid()) continue;

		const FString Type = AObj->GetStringField(TEXT("Type"));
		const FString Name = AObj->GetStringField(TEXT("Name"));

		FVector Loc = FVector::ZeroVector;
		FRotator Rot = FRotator::ZeroRotator;
		FVector Scale = FVector::OneVector;
		ParseTransformFromJson(AObj, Loc, Rot, Scale);

		FTransform SpawnTransform(Rot, Loc, Scale);
		FActorSpawnParameters SpawnParams;
		SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;

		if (Type == TEXT("StaticMesh"))
		{
			FString MeshJson;
			if (AObj->TryGetStringField(TEXT("MeshJson"), MeshJson) && !MeshJson.IsEmpty())
			{
				if (UStaticMesh* SM = ImportStaticMeshFromJson(MeshJson))
				{
					TArray<UMaterialInterface*> ResolvedOverrides;
					const TArray<TSharedPtr<FJsonValue>>* OverrideMats = nullptr;
					if (AObj->TryGetArrayField(TEXT("Materials"), OverrideMats) && OverrideMats)
					{
						ResolvedOverrides.SetNumZeroed(OverrideMats->Num());
						for (int32 MatIdx = 0; MatIdx < OverrideMats->Num(); ++MatIdx)
						{
							FString OverrideMatJson;
							if ((*OverrideMats)[MatIdx].IsValid() && (*OverrideMats)[MatIdx]->TryGetString(OverrideMatJson) && !OverrideMatJson.IsEmpty())
							{
								ResolvedOverrides[MatIdx] = ImportMaterialFromJson(OverrideMatJson);
							}
						}
					}

					auto SpawnMeshInstance = [&](const FTransform& Xform, const FString& Label)
					{
						if (AStaticMeshActor* Actor = EditorWorld->SpawnActor<AStaticMeshActor>(AStaticMeshActor::StaticClass(), Xform, SpawnParams))
						{
							Actor->SetActorLabel(Label);
							UStaticMeshComponent* SMC = Actor->GetStaticMeshComponent();
							SMC->SetMobility(EComponentMobility::Movable);
							SMC->SetStaticMesh(SM);
							for (int32 MatIdx = 0; MatIdx < ResolvedOverrides.Num(); ++MatIdx)
							{
								if (ResolvedOverrides[MatIdx])
								{
									SMC->SetMaterial(MatIdx, ResolvedOverrides[MatIdx]);
								}
							}
						}
					};

					const TArray<TSharedPtr<FJsonValue>>* InstancesArr = nullptr;
					if (AObj->TryGetArrayField(TEXT("Instances"), InstancesArr) && InstancesArr && InstancesArr->Num() > 0)
					{
						for (int32 InstIdx = 0; InstIdx < InstancesArr->Num(); ++InstIdx)
						{
							const TSharedPtr<FJsonObject> IObj = (*InstancesArr)[InstIdx]->AsObject();
							if (!IObj.IsValid()) continue;
							FVector ILoc; FRotator IRot; FVector IScale;
							ParseTransformFromJson(IObj, ILoc, IRot, IScale);
							SpawnMeshInstance(FTransform(IRot, ILoc, IScale), FString::Printf(TEXT("%s_%d"), *Name, InstIdx));
						}
					}
					else
					{
						SpawnMeshInstance(SpawnTransform, Name);
					}
				}
			}
		}
		else if (Type == TEXT("Niagara"))
		{
			FString NiagaraJson;
			if (AObj->TryGetStringField(TEXT("NiagaraJson"), NiagaraJson) && !NiagaraJson.IsEmpty())
			{
				if (ImportNiagaraFromJson(NiagaraJson))
				{
					TSharedPtr<FJsonObject> NObj = LoadJsonObjectFromFile(NiagaraJson);
					const FString SysName = SanitizeAssetName(NObj.IsValid() ? NObj->GetStringField(TEXT("Name")) : Name);
					const FString SysPath = FString::Printf(TEXT("/Game/UExplorer/Niagara/%s.%s"), *SysName, *SysName);
					if (UNiagaraSystem* Sys = LoadObject<UNiagaraSystem>(nullptr, *SysPath))
					{
						if (ANiagaraActor* NA = EditorWorld->SpawnActor<ANiagaraActor>(ANiagaraActor::StaticClass(), SpawnTransform, SpawnParams))
						{
							NA->SetActorLabel(Name);
							if (UNiagaraComponent* NC = NA->GetNiagaraComponent())
							{
								NC->SetAsset(Sys);
							}
						}
					}
				}
			}
		}
		else if (Type == TEXT("ScenePointLight"))
		{
			if (APointLight* PL = EditorWorld->SpawnActor<APointLight>(APointLight::StaticClass(), SpawnTransform, SpawnParams))
			{
				PL->SetActorLabel(Name);
				PL->PointLightComponent->SetIntensity(AObj->GetNumberField(TEXT("Intensity")));
				PL->PointLightComponent->SetAttenuationRadius(AObj->GetNumberField(TEXT("AttenuationRadius")));
				ApplyLightColor(PL->PointLightComponent.Get(), AObj);
			}
		}
		else if (Type == TEXT("SceneSpotLight"))
		{
			if (ASpotLight* SL = EditorWorld->SpawnActor<ASpotLight>(ASpotLight::StaticClass(), SpawnTransform, SpawnParams))
			{
				SL->SetActorLabel(Name);
				SL->SpotLightComponent->SetIntensity(AObj->GetNumberField(TEXT("Intensity")));
				SL->SpotLightComponent->SetAttenuationRadius(AObj->GetNumberField(TEXT("AttenuationRadius")));
				double Cone = 0.0;
				if (AObj->TryGetNumberField(TEXT("InnerConeAngle"), Cone)) SL->SpotLightComponent->SetInnerConeAngle(static_cast<float>(Cone));
				if (AObj->TryGetNumberField(TEXT("OuterConeAngle"), Cone)) SL->SpotLightComponent->SetOuterConeAngle(static_cast<float>(Cone));
				ApplyLightColor(SL->SpotLightComponent.Get(), AObj);
			}
		}
		else if (Type == TEXT("SceneRectLight"))
		{
			if (ARectLight* RL = EditorWorld->SpawnActor<ARectLight>(ARectLight::StaticClass(), SpawnTransform, SpawnParams))
			{
				RL->SetActorLabel(Name);
				RL->RectLightComponent->SetIntensity(AObj->GetNumberField(TEXT("Intensity")));
				double Dim = 0.0;
				if (AObj->TryGetNumberField(TEXT("SourceWidth"), Dim)) RL->RectLightComponent->SetSourceWidth(static_cast<float>(Dim));
				if (AObj->TryGetNumberField(TEXT("SourceHeight"), Dim)) RL->RectLightComponent->SetSourceHeight(static_cast<float>(Dim));
				ApplyLightColor(RL->RectLightComponent.Get(), AObj);
			}
		}
		else if (Type == TEXT("DirectionalLight"))
		{
			if (ADirectionalLight* DL = EditorWorld->SpawnActor<ADirectionalLight>(ADirectionalLight::StaticClass(), SpawnTransform, SpawnParams))
			{
				DL->SetActorLabel(Name);
				if (UDirectionalLightComponent* DLC = Cast<UDirectionalLightComponent>(DL->GetLightComponent()))
				{
					DLC->SetIntensity(AObj->GetNumberField(TEXT("Intensity")));
					ApplyLightColor(DLC, AObj);
				}
			}
		}
		else if (Type == TEXT("SkyLight"))
		{
			if (ASkyLight* Sky = EditorWorld->SpawnActor<ASkyLight>(ASkyLight::StaticClass(), SpawnTransform, SpawnParams))
			{
				Sky->SetActorLabel(Name);
				if (USkyLightComponent* SLC = Sky->GetLightComponent())
				{
					SLC->SetIntensity(AObj->GetNumberField(TEXT("Intensity")));
					ApplyLightColor(SLC, AObj);
				}
			}
		}
	}

	UEditorLoadingAndSavingUtils::SaveMap(EditorWorld, MapAssetPath);
	UE_LOG(LogUExplorerImporter, Display, TEXT("Saved imported world to new map '%s' (%d actors)"), *MapAssetPath, Actors->Num());
	return true;
}
