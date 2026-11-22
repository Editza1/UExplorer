#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

class UMaterial;
class UTexture2D;

/**
 * Native UE5 C++ DXBC Pixel Shader -> UE5 Material Expression Node Translator.
 * Translates cooked DXBC shader assembly into 1:1 connected UE5 MaterialExpression nodes,
 * and includes a built-in ElimDevice test harness (M_Mole_ElimDevice_Ring + NS_Mole_ElimDevice_Idle).
 */
class UEXPLORERBRIDGE_API FUExplorerShaderTranslator
{
public:
	/**
	 * Translates a cooked DXBC PixelShaderAsm string (plus parameter metadata and imported textures)
	 * directly into connected UE5 UMaterialExpression nodes on TargetMaterial.
	 * Returns true if at least one live output pin was reconstructed and connected.
	 */
	static bool TranslateMaterialShader(
		UMaterial* TargetMaterial,
		const FString& PixelShaderAsm,
		const TSharedPtr<FJsonObject>& MatObj,
		const TMap<FString, UTexture2D*>& ImportedTextures);

	/**
	 * Built-in test harness: loads M_Mole_ElimDevice_Ring (from Exports or embedded test shader)
	 * and NS_Mole_ElimDevice_Idle into /Game/UExplorer/Tests/ so we can verify 1:1 translation inside UE 5.8.
	 */
	static void RunElimDeviceTest();
};
