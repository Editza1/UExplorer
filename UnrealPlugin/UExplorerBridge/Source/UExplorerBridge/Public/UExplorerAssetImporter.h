#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

class UTexture2D;
class UMaterialInterface;
class UStaticMesh;

class UEXPLORERBRIDGE_API FUExplorerAssetImporter
{
public:
	static bool ImportManifestFile(const FString& ManifestPath);
	static bool ImportManifestJson(const FString& JsonText);

	static UTexture2D* ImportTexturePng(const FString& PngFilePath, const FString& DestFolder, bool bIsNormalMap = false, bool bIsMask = false);
	static UMaterialInterface* ImportMaterialFromJson(const FString& MaterialJsonPath, bool bForceRebuild = false);
	static UStaticMesh* ImportStaticMeshFromJson(const FString& MeshJsonPath);
	static bool ImportNiagaraFromJson(const FString& NiagaraJsonPath);
	static bool ImportWorldFromJson(const FString& WorldJsonPath);
	static FString SanitizeAssetName(const FString& InName);

private:
	static TSharedPtr<FJsonObject> LoadJsonObjectFromFile(const FString& FilePath);
};
