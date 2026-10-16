"GameInfo"
{

	game 		"Counter-Strike 2"
	title 		"Counter-Strike 2"
	title_pw	"E58F8DE68190E7B2BEE88BB1EFBC9AE585A8E79083E694BBE58ABF"
	
	SatelliteDir	csgo_gc

	FileSystem
	{
		SteamAppId				710			// This will mount all the GCFs we need (240=CS:S, 220=HL2).
		SearchPaths
		{
			Game_LowViolence	csgo_lv // Perfect World content override

            Game	csgo/overrides/botprofile.vpk

            Game	csgo/addons/metamod

			Game	csgo
			Game	core

			Mod		csgo

			AddonRoot			csgo_addons
			OfficialAddonRoot	csgo_community_addons
		}

		"UserSettingsPathID"	"USRLOCAL"
		"UserSettingsFileEx"	"cs2_"
	}

	Engine2
	{
		"HasModAppSystems" "1"
		"Capable64Bit" "1"
		"URLName" "csgo"
		"DefaultRenderSystem"					"-dx11"
		"DefaultToolsRenderSystem"				"-dx11"
		"RenderingPipeline"
		{
			"SkipPostProcessing" "0"
			"SupportsMSAA" "1"
			//"SkipGameOverlay" "1"
			"PostProcessingInMainPipeline" "1"
			"ToolsVisModes" "1"
			"OpaqueFade" "1"
			"AmbientOcclusionProxies" "1"
			"HighPrecisionLighting" "1" 

			// Defaults for maps without a env_tonemap_controller
			"Tonemapping_DefaultAutoExposureMin" "1.0"
			"Tonemapping_DefaultAutoExposureMax" "1.0"
			"Tonemapping_DefaultFilmicLinear" "1"
		}
		"SetUILanguageOnSteamDropDown" "1" // csgo language is controlled by steam, both audio and ui localization strings
		"HasLegacyGameUI" "1" // csgo uses some legacy gameui systems
		// Default MSAA sample count when run in non-VR mode
		"MSAADefaultNonVR"	"4" 
		"DefensiveConCommands"	"1"
		"PauseSinglePlayerOnGameOverlay" "1"
		"PauseOnCtrlConsole" "0" // Src2 issues a 'setpause' on holding down CTRL + toggleconsole key, disable this for CS2.
		"RestrictConsoleCloseKey" "`"	// This setting stipulates that if the binding for toggleconsole is set to anything other
										// than the default KEY_BACKQUOTE, then it only works for opening the console, not closing.
										// ESC can always be used to close the console regardless of this setting.
										// This matches csgo src1 behaviour.

		"ThreadPoolCoreTypeRequest" "undifferentiated"
		"DepotBuildDateTimeInTitleBar" "1"
		"InitFilterTextEarly" "1"
		"CNPW"	"CD535060BE7CF1821AFF685103743B65BF52"
		"LvConfig"	"0"
	}
	
	InputSystem
	{
		"ButtonCodeIsScanCode"		"1"
		"LockButtonCodeIsScanCode"	"1"
	}

	pulse
	{
		"pulse_enabled"					"1"
	}

	DelayedConCommands
	{
		"connect_lobby" "1"
		"connect" "1"
		"playcast" "1"
		"csgo_econ_action_preview" "1"
		"csgo_download_match" "1"
		"playdemo" "1"
		"gcconnect" "1"
	}

	ConVars
	{
		"cl_joystick_enabled" "0"
		"panorama_joystick_enabled" "0"
		"demo_max_consecutive_skip_packets" "2500"
		"r_particle_batch_collections" "1"
		"spec_replay_enable"
		{
			"min"		"0"
			"max"		"0"
		}
		// Bandwidth control default: 300,000 Bps
		"rate"
		{
			"min"		"98304"
			"default"	"786432"
			"max"		"1000000"
		}
		"sv_minrate"	"98304"
		"sv_maxunlag"	"0.200"

		"cl_interp_ratio" "0"

		// GOTV controls
		"tv_secret_code"		"0"
		"tv_relay_secret_code"	"0"
		"tv_update_hibernation_enabled" "0"

		// Performance
		"sv_parallel_checktransmit"		"2"
		"fps_max"		"400"
		"fps_max_ui"
		{
			"default"	"200"
			"version"	"2"
		}
		"r_add_views_in_pre_output"		"1"

		// Nav fixups
		"nav_path_fixup_climb_up_segments" "1"
		"nav_gen_agent_radius_buffer" "0.75"
		"nav_gen_jump_connection_min_overlap_ratio" "0.1"

		// CSM override
		"csm_slope_scale_db_override" "3"
		
		// SSAO customization for CSGO (this is used on viewmodels)
		"r_ssao_radius"				"8"
		"r_ssao_strength"			"3"
		"r_ssao_bias"				"2.5"

		// this cache kills performance due to mutex contention
		"bone_decode_cache_enabled" "0"

		// Disable warning about oscillating panorama classes
		"panorama_classes_oscillation_warning" "0"

		// Spew warning when adding/removing classes to/from the top of the hierarchy
		"panorama_classes_perf_warning_threshold_ms" "0.75"

		// Panorama - enable render target cache
		"panorama_disable_render_target_cache" "0"

		// Panorama - enable minidumps on JS exceptions
		"panorama_js_minidumps" "1"

		// Panorama - use f6 to close and open debugger
		// (Otherwise f6 just opens debugger, and a subsequent f6 does an inspect)
		"panorama_toggledebugger_mode" "1"

		// HLTV AutoDirector - disable it for now so that it doesn't interfere with our spectator camera during replays / hltv / demos
		// Needs to be revisited when we re-enable AutoDirector
		"spec_autodirector" "false"

		// Grass
		"r_grass_quality"				"3"
		"r_grass_alpha_test"			"1"
		"r_grass_density_mode"			"1"
		"r_grass_start_fade"			"3000"
		"r_grass_end_fade"				"3900"

		// Disable smooth morph normals
		"r_smooth_morph_normals"		"0"

		// Default to binding keys based on keyboard position instead of key name
		"input_button_code_is_scan_code"		"1"
		"input_button_code_is_scan_code_scd"	"1"

		// Disable Cubemap Brightening
		"lb_cubemap_normalization_max" 		"1"

		// For low quality shaders, cubemap bounds are scaled by this percentage of the fade region
		"lb_low_quality_shader_fade_region_rescale"	"0.5"
		
		// Use normal quality compression even in MET, this makes compiles in MET slower than
		// the default of fastest (0), but reduces artifacts that are confusing to artists since 
		// it's not clear that texture compression quality is different in MET than when regularly compiled.
		"rc_default_texture_encode_quality" "2"

		// The engine default of 50 for CS:GO is too high, drop down to a more sensible 
		// default value.
		"mouse_pitchyaw_sensitivity"	"3"
		"pitch_extra_mouse_sensitivity"	"1.0"

		"r_size_cull_threshold"			"0.33"
		"r_size_cull_threshold_fade"	"7.5"
		"inferno_scorch_decals" "0"

		// Steam Audio project specific convars
		"snd_musicvolume"
		{
			"version"	"2"
		}
		"snd_steamaudio_enable_custom_hrtf"					"0"
		"snd_steamaudio_enable_perspective_correction"		"1"
		"snd_steamaudio_perspective_correction_factor"		"1.0"
		"snd_steamaudio_normalize_default_hrtf_volume"		"1"
		"snd_steamaudio_default_hrtf_volume_gain"			"0.0"
		"snd_hrtf_distance_behind"							"50"
		"snd_steamaudio_max_hrtf_normalization_gain_db"		"6.0"
		"snd_steamaudio_enable_pathing"						"1"
		"snd_steamaudio_source_pathing_debug"				"0"
		"snd_steamaudio_max_probes_customdata"				"12000"
		"snd_steamaudio_baked_occlusion_mode"				"6"
		"snd_steamaudio_load_reverb_data"					"0"
		"snd_steamaudio_load_pathing_data"					"0"
		"snd_steamaudio_load_dimensions_data"				"0"
		"snd_steamaudio_load_materials_data"				"0"
		"snd_steamaudio_load_occlusion_data"				"0"
		"snd_use_baked_occlusion"							"0"
		"snd_steamaudio_use_soundblocking_shapes_only"		"0"
		"snd_steamaudio_baked_occlusion_pathing_exponent"			"0.2"
		"snd_steamaudio_baked_occlusion_probelookup_usealternate"	"1"
		"snd_steamaudio_baked_dimensions_probelookup_usealternate"	"1"
		"snd_steamaudio_baked_occlusion_reflection_factor"			"10"
		"snd_steamaudio_enabe_append_probes_to_cover_navmesh"		"1"
		"snd_steamaudio_debug_cover_navmesh_probe_size_scale"		"1.0"
		"snd_steamaudio_custombake_occlusion_numsimulations"		"2"
		"snd_steamaudio_custombake_occlusion_bidirectional"			"1"
		"snd_steamaudio_baked_occlusion_reflection_regularization"	"1e-4"
		"snd_steamaudio_diagnostic_baked_occlusion_single_probe"	"-1"
		"snd_steamaudio_baked_occlusion_air_absorption_coefficient"		"0.006"
		"snd_steamaudio_baked_occlusion_spatialfilter_mode"				"1"
		"snd_steamaudio_baked_occlusion_spatialfilter_radius_factor"	"2.0"
		"snd_steamaudio_occlusionvisualization_sampleheight"			"1.5"
		"snd_steamaudio_occlusionvisualization_sampleradius"			"4.0"

		"snd_event_browser_default_stack"			"csgo_mega"
		"snd_event_browser_default_vsnd_field"		"public.vsnd_files_track_01"

		// Need much tighter sound clock sync
		"snd_delay_sound_ms_max"	"40"
		
		// possible tighter sound clock sync settings. needs testing. 
		//"snd_delay_sound_ms_max"	"20"
		//"snd_delay_sound_ms_shift" "5"

		//don't let people mess with speaker config settings.
		"speaker_config"
		{
			"min"		"-1"
			"default"	"-1"
			"max"		"-1"
		}

		"cl_disconnect_voice_fade"	"-1.0"
		"cl_disconnect_soundevent"	"StopSoundEvents.StopAllExceptMusic"
		
		// Physics specific customization
		"phys_use_position_based_toi_test" "1"

		// VOIP Settings.
		"voice_in_process"	"1"
		"voice_threshold"
		{
			"version" "2"
		}

		"sv_long_frame_ms" "15"
		"cq_buffer_bloat_msecs_max" "64"

		"cl_interp_ag2_for_non_ag2_entities"	"0"

		"r_skip_precache_validation_check" "1"

		"r_aoproxy_default_light_position_0" "1.0 0.0 1.5"
		"r_aoproxy_default_light_position_1" "-1.0 0.0 1.5"
		"r_aoproxy_default_light_position_2"  "0.0 1.0 1.5"
		"r_aoproxy_default_light_position_3" "0.0 -1.0 1.5"
		"r_aoproxy_default_light_cone_angles" "0.3 0.3 0.3 0.3"
		"r_aoproxy_default_light_cone_strengths" "1.0 1.0 1.0 1.0"
		"r_aoproxy_default_ambient_strength" "0.2"
	}
	
	Sounds
	{
		HierarchicalEncodingFiles	 "1"
	}

	ResourceCompiler
	{
		"DeprecatedBehaviorVersionsAllowed"	"1" // Inherited from the former csgo_imported mod.

		SoundStackScripts
		{
			CompilerVersion "1" // Inherited from the former csgo_imported mod.
		}

		// Overrides of the default builders as specified in code, this controls which map builder steps
		// will be run when resource compiler is run for a map without specifiying any specific map builder
		// steps. Additionally this controls which builders are displayed in the hammer build dialog.
		DefaultMapBuilders
		{			
			"bakedlighting"	"1"	// Enable lightmapping during compile time
			"nav"		"1"	// Generate nav mesh data
			"light"		"0"	// Using per-vertex indirect lighting baked from within hammer
			"envmap"	"0"	// this is broken
			"sareverb"	"1" // Bake Steam Audio reverb
			"sapaths"	"1" // Bake Steam Audio pathing info
			"sacustomdata"	"1"	// Bake Steam Audio custom data
		}
		GameSpecificPostMapBuildSteps
		{
			"cs2_bomb_damage"      "1"
		}
		CompileManifest
		{
			EnforceValidManifestResourcePaths "1"
		}
		MeshCompiler
		{
			PerDrawCullingData      "1"
			EncodeVertexBuffer      "1"
			EncodeVertexBufferVersion   "1"
			EncodeVertexBufferLevel     "3"
			EncodeIndexBuffer       "1"
			UseMikkTSpace           "1"
			MeshletConeWeight       ".15"
			SplitDepthStream		"1"
		}
		WorldRendererBuilder
		{
			FixTJunctionEdgeCracks  		"1"
			VisibilityGuidedMeshClustering		"1"
			MinimumTrianglesPerClusteredMesh	"2048"
			MinimumVerticesPerClusteredMesh		"2048"
			MinimumVolumePerClusteredMesh		"1800"		// ~12x12x12 cube
			MaxPrecomputedVisClusterMembership	"16"
			UseAggregateInstances			"1"
			AggregateInstancingMeshlets			"1"
			UseStaticEnvMapForObjectsWithLightingOrigin	"1"
			BakePropsWithNonUniformScale "1"
		}
		// Optimisation for Hammer Mesh Physics

		PhysicsBuilder
		{
			DefaultHammerMeshSimplification		"0.0"
		}
		BakedLighting
		{
			Version 2
			DeterministicBuild 1
			DisableCullingForShadows 1
			MinSpecLightmapSize 4096
			LightmapGutterSize 1 // MinSpecLightmapSize is 4k, at 8k this inflates to 2, enough for bicubic filtering
			
			ImportanceVolumeTransitionRegion 120            // distance we transition from high to low resolution charts 
			                                                // when a triangle is outside an importance volume
			LPVAtlas 1
			LPVOctree 0
			LightmapChannels
			{
				irradiance 1
				direct_light_shadows 1

				directional_irradiance
				{
					MaxResolution 4096
					CompressedFormat DXT1
				}

				debug_chart_color
				{
					MaxResolution 4096
					CompressedFormat DXT1
				}
			}
		}
		VisBuilder
		{
			MaxVisClusters "4096"
			PreMergeOpenSpaceDistanceThreshold "128.0"
			PreMergeOpenSpaceMaxDimension "2048.0"
			PreMergeOpenSpaceMaxRatio "8.0"
			PreMergeSmallRegionsSizeThreshold "20.0"
			DeterministicBuild "1"
		}
		TextureCompiler
		{
			Compression             "lz4"
			CompressMipsOnDisk      "1"
			CompressMinRatio        "95"
			AllowNP2Textures		"1"
			AllowPanoramaMipGeneration	"1"
			PublicToolsDefaultMaxRes "2048"
		}
		SteamAudio
		{
			ReverbDefaults
			{
				GridGenerationType	"0"						// 0: Automatic, Everywhere, 1: Automatic, Use Probe Generation Volume, 2: Manual
				FilterUsingVolumes	"1"						// Filter Using Probe Exclusion Volumes ( boolean )
				FilterUsingNavMesh	"0"						// Filter Using NavMesh
				GridSpacing			"3.0"
				HeightAboveFloor	"1.5"
				RebakeOption		"1"						// 0: cleanup, 1: manual, 2: auto
				NumRays				"32768"
				NumBounces			"64"
				IRDuration			"1.0"
				AmbisonicsOrder		"1"
				ClusteringEnabled	"0"
				ClusteringCubemapResolution	"16.0"
				ClusteringDepthThreshold	"10.0"
				CompressionEnabled	"0"
				CompressionQuality	"0.95"
			}
			PathingDefaults
			{
				GridGenerationType	"0"						// 0: Automatic, Everywhere, 1: Automatic, Use Probe Generation Volume, 2: Manual
				FilterUsingVolumes	"1"						// Filter Using Probe Exclusion Volumes ( boolean )
				FilterUsingNavMesh	"0"						// Filter Using NavMesh
				GridSpacing			"3.0"
				HeightAboveFloor	"1.5"
				RebakeOption		"1"						// 0: cleanup, 1: manual, 2: auto
				NumVisSamples		"1"
				ProbeVisRadius		"0"
				ProbeVisThreshold	"0.1"
				ProbeVisPathRange	"1000.0"
			}
			CustomDataDefaults
			{
				GridGenerationType	"0"						// 0: Automatic, Everywhere, 1: Automatic, Use Probe Generation Volume, 2: Manual
				FilterUsingVolumes	"1"						// Filter Using Probe Exclusion Volumes ( boolean )
				FilterUsingNavMesh	"0"						// Filter Using NavMesh
				GridSpacing			"3.0"
				HeightAboveFloor	"1.5"
				RebakeOption		"1"						// 0: cleanup, 1: manual, 2: auto
				BakeOcclusion		"0"						// 0: Disabled, 1: Enabled
				BakeDimensions		"0"						// 0: Disabled, 1: Enabled
				BakeMaterials		"0"						// 0: Disabled, 1: Enabled
				OcclusionPathing			"1"
				OcclusionReflection			"0"
				OcclusionReflectionRays		"16384"
				OcclusionReflectionBounces	"16"
				DimensionsOutsideThreshold	".1"
				DimensionsOutsideFieldOrder "1"
				DimensionsInsideThreshold	"1"
				DimensionsSizeThreshold		"128"
				DimensionsInsideSizeFieldOrder	".1"
			}
		}
	}


	GMS
	{
		"Advertise"										"1"
		"RequireLoginForDedicatedServers"				"1"
	}
	
	GameInstructor
	{
		"SaveToSteamStats" "1"
	}

	SupportedLanguages
	{
		"brazilian" "3"
		"bulgarian" "3"
		"czech" "3"
		"danish" "3"
		"dutch" "3"
		"english" "3"
		"finnish" "3"
		"french" "3"
		"german" "3"
		"greek" "3"
		"hungarian" "3"
		"italian" "3"
		"indonesian" "3"
		"japanese" "3"
		"koreana" "3"
		"latam" "3"
		"norwegian" "3"
		"polish" "3"
		"portuguese" "3"
		"romanian" "3"
		"russian" "3"
		"schinese" "3"
		"spanish" "3"
		"swedish" "3"
		"tchinese" "3"
		"thai" "3"
		"turkish" "3"
		"ukrainian" "3"
		"vietnamese" "3"
	}
	
	CS2WorkshopManager
	{
		"RequiredTag" "CS2"
		"HighlightEntriesMissingRequiredTag" "1"
	}
	
	AssetBrowser
	{
		retail_filter0		"characters/models/"
		retail_filter1		"materials/decals/sprays/"
		retail_filter2		"panorama/"
		retail_filter3		"patches/"
		retail_filter4		"stickers/"
		retail_filter5		"weapons/"
		retail_filter6		"materials/models/inventory_items/"
	}

	AddonConfig	
	{
		"VpkDirectories"
		{
			"exclude"       "maps/content_examples"
			"include"       "maps"
			"include"       "cfg/maps"
			"include"       "materials"
			"include"       "models"
			"include"       "panorama/images/overheadmaps"
			"include"       "panorama/images/map_icons"
			"include"       "panorama/images/custom_game"
			"include"       "panorama/layout/custom_game"
			"include"       "panorama/styles/custom_game"
			"include"       "particles"
			"include"       "resource/overviews"
			"include"       "scripts"
			"include"       "sounds"
			"include"       "soundevents"
			"include"       "lighting/postprocessing"
			"include"       "postprocess"
			"include"       "addoninfo.txt"
		} 
		"AllowAddonDownload" "1"
		"AllowAddonDownloadForDemos" "1"
		"DisableAddonValidationForDemos" "1"
		"UseOfficialAddons" "1"
		"RestrictFlatFileAddonsToTools" "1"
	}

	MaterialEditor
	{
		"DefaultShader" "csgo_simple"
		"DefaultAutoPromptForShaderOnNewMaterialCreation" "1"
		"DetailPropsEnabled" "1"
	}

	//====================================================================================
	// Merged from the former csgo_core mod.
	//====================================================================================
	type		multiplayer_only
	nomodels 1
	nohimodel 1
	l4dcrosshair 1
	nodegraph 0
	perfwizard 0
	tonemapping 1 // Show tonemapping ui in tools mode
	configconflictresolutiondialog 0
	GameData	"csgo.fgd"
	hidden_maps
	{
		"test_speakers"			1
		"test_hardware"			1
	}
	MaterialSystem2
	{
		RenderModes
		{
			"game" "Default"
			"game" "CsgoForward"
			"game" "Depth"
			"game" "ProjectionDepth"
			"game" "Decals"
			"game" "Forward"
			"game" "GBuffer" // For Particle Shadows
			"game" "FirstpersonLegsPrepass"

			"dev" "ToolsShadingComplexity"
			"dev" "ToolsVis" // Visualization modes for all shaders (lighting only, normal maps only, etc.)
			"dev" "ToolsWireframe" // This should use the ToolsVis mode above instead of being its own mode
			"tools" "ToolsUtil" // Meant to be used to render tools sceneobjects that are mod-independent, like the origin grid
		}

		ToolsShadingComplexity
		{
			"TargetRenderMode" "CsgoForward"
		}

		ShaderIDColors
		{
			"generic.vfx" "255 255 255"

			"csgo_simple.vfx" "128 128 128"
			"csgo_complex.vfx" "64 32 128"
			"csgo_vertexlitgeneric.vfx" "240 0 0"
			"csgo_unlitgeneric.vfx" "240 32 192"
			"csgo_lightmappedgeneric.vfx" "128 0 0"

			"csgo_character.vfx" "0 0 255"
			"csgo_static_overlay.vfx" "0 255 255"
			"csgo_projected_decals.vfx" "0 128 128"
			"csgo_environment.vfx" "128 192 64"
			"csgo_environment_blend.vfx" "64 128 0"
			"csgo_glass.vfx" "128 32 128"
			"csgo_weapon.vfx" "192 128 64"
			"csgo_water_fancy.vfx" "64 128 240"

			"cables.vfx" "128 64 64"
			"spritecard.vfx" "240 240 0"
		}

		"ErrorMaterialIsFatalError"	"1"
	}
	Panorama
	{
		"AllowGlobalPanelContext" "1"
		"HtmlUserAgent" "CSGO Client"
		"AllowCustomGameUI" 1
	}
	NetworkSystem
	{
		PublicUniverse
		{
			"NetworkConfigLimits"	"1"
		}

		BetaUniverse
		{
			"FakeLag"			"40"
			"FakeLoss"			".1"
			"FakeJitter"		"low"

			"TimeTable"
			{
				// LAN conditions at noon local time
				"120000"
				{
					"FakeLag"			"0"
					"FakeLoss"			"0"
					"FakeReorderPct"	"0"
					"FakeReorderDelay"	"0"
					"FakeJitter"		"off"
				}
				// Back to more realistic internet conditions 2:45:00 PM local time
				"144500"
				{
				}
			}
		}
	}
	Particles
	{
		"ParticlesFoggedByDefault"	"1"
		"EnableParticleShaderFeatureBranching"	"1"
		"ParticleTextureBaseSlot" "8"
		"EnableMixedResolution" "1"
		"ParticleTraceOffsetOnlyHit" "1"
		"PET_SupportFadingOpaqueModels" "1"
	}
	SceneSystem
	{
		"GpuLightBinner" "1"
		"GpuLightBinnerSunLightFastPath" "1"
		"GpuLightBinnerBinEnvMaps" "1"
		"GpuLightBinnerBinLPVs" "1"
		"GpuLightBinnerSupportViewModelCascade" "1"
		"DynamicShadowResolution" "1"
		"DefaultShadowTextureWidth" "4096"
		"DefaultShadowTextureHeight" "4096"
		"ShadowTextureImageFormat_D16" "1"		// would like to enable this for CS2 soon to test D16 shadows (separately from SparseShadowTrees)
		"SparseShadowTrees" "1"					// enable this to experiment with Sparse Shadow Trees as a drop in replacement for static geo shadow rendering into cascades
		"PointLightShadowsEnabled" "1"
		"Tonemapping"	"1"
		"NonTexturedGradientFog" "1"
		"CubemapFog" "1"
		"BloomEnabled" "1"
		"HDRFrameBuffer" "1"
		"DisableShadowFullSort" "1"
		"PerObjectLightingSetup" "1"
		"CharacterDecals" "1"
		"FirstpersonLegs" "1"
		"SupportsHybridInstancedFade" "1"

		"CSMCascadeResolution" "2048"
		"SunLightManagerCount" "0"
		"SunLightManagerCountTools" "0"

		"LightCookieAllocGranularity" "1"
		"LightCookieMinAllocSize" "0"

		WellKnownLightCookies
		{
			"blank" "materials/effects/lightcookies/blank.vtex"
			"flashlight" "materials/effects/lightcookies/flashlight.vtex"
			"muzzleflash" "materials/effects/lightcookies/muzzleflash.vtex"
		}

		"TransformTextureRowCount" "512"
		"TransformTextureRowCountToolsMode" "8192"
		"CMTAtlasWidth" "1024"
		"CMTAtlasHeight" "512"
		"CMTAtlasChunkSize" "128"

		"DynamicDecalsUseShrinkWrap" "1"	// enable shrinkwrap optimization for dynamic decal materials using F_FASTAPPROX

		"ComputeShaderSkinning" "1"
	}
	ToolsEnvironment
	{
		"Engine"	"Source 2"
		"ToolsDir"	"../sdktools"	// NOTE: Default Tools path. This is relative to the mod path.
		"RestrictWorkshopItemTools"	"1"
	}
	Hammer
	{
		fgd_files
		{
			"csgo.fgd"					"1"
			"csgo_internal.fgd"			"1"
		}
		"GameFeatureSet"				"CounterStrike"
		"DefaultTextureScale"			"0.125000"
		"DefaultSolidEntity"			"trigger_multiple"
		"DefaultPointEntity"			"info_player_start"
		"NavMarkupEntity"				"func_nav_markup"
		"RenderMode"					"ToolsVis"
		"TileMeshesEnabled"				"1"
		"TileGridSupportsBlendHeight"	"1"
		"TileGridBlendDefaultColor"		"0 255 0"
		"LoadScriptEntities"			"0"
		"UsesBakedLighting"				"1"
		"ShadowAtlasWidth"				"6144"
		"ShadowAtlasHeight"				"6144"
		"TimeSlicedShadowMapRendering"	"1"
		"TerrainTools"					"1"
		"DefaultGrassMaterial"			"materials/grass/grassquad1.vmat"
		"SteamAudioEnabled"				"1"
		"AddonMapCommand"				"map_workshop"
		"LatticeDeformerEnabled"		"1"
		"SmartPropInstanceRendering"	"1"
	}
	RenderPipelineAliases
	{
		"Tools"			"CSGO"
		"EnvMapBake"	"CSGO"
	}
	Source1Import
	{
		"importmod"			"csgo"
		"importdir"			"."

		"createStaticOverlays" 	  "1"	// if "1" create static overlays from s1 info_overlays, if "0" will treat them as s2 projected decals.
		"createPathParticleRopes" "1"   // convert s1 move_rope/keyframe_rope chained entities to path_particle_rope system in s2.
		"fixup3DSkybox" 		  "1"   // if a func_instance contains a 3d skybox vmf (contains a sky_camera entity), does the s2 fixup accordingly, 
										// otherwise the skybox will end up part of the main map and will need to be fixed up by hand 
										// (as is the common case in s1 where maps have the skybox already part of the main map).
		"removeHiddenNodes"		  "1"   // 0 => hidden nodes in vmf are imported but marked as visible=false, startenabled=false, 1 => ignore hidden nodes on import
		"loadBSPDetailData"       "1"	// 1 => Load map file .bsp for detail object system (foliage) data.
	}
	BugReporter
	{
		"AutoBugProduct" "CS:GO"
		"AutoBugComponent" "Source2"
	}
	NavSystem
	{
		"NavTileSize" "128.0"
		"NavCellSize" "1.5"
		"NavCellHeight" "2.0"

		// Hull definitions live in scripts/nav_hulls.vdata
		// Preset definitions live in scripts/nav_hulls_presets.vdata
		"NavHullsPreset" "default"

		"NavRegionMinSize" "8"
		"NavRegionMergeSize" "20"
		"NavEdgeMaxLen" "1200"
		"NavEdgeMaxError" "45.0"
		"NavVertsPerPoly" "4"
		"NavDetailSampleDistance" "120.0"
		"NavDetailSampleMaxError" "2.0"
		"NavSmallAreaOnEdgeRemovalSize" "-1.0"
	}
	CustomNavBuild
	{
		ModuleName "server.dll"
		Interface  "customnavsystem001"
	}
	WorldRenderer
	{
		"IrradianceVolumes"		"0"
		"EnvironmentMaps"		"1"
		"EnvironmentMapBlurType"  "GGX"
		"EnvironmentMapFaceSize"	"256"
		"EnvironmentMapRenderSize"	"1024"
		"EnvironmentMapFormat"		"BC6H"
		"EnvironmentMapPreviewFormat"	"BC6H"
		//"EnvironmentMapPreviewFormat" "RGBA16161616F"
		"EnvironmentMapColorSpace"	"linear"
		"EnvironmentMapMipProcessor"	"GGXCubeMapBlur"
		// Build cubemaps into a cube array instead of individual cubemaps?
		"EnvironmentMapUseCubeArray"	"1"
		// instead of dynamically creating an atlas, we require a single array (built offline, see
		// EnvironmentMapUseCubeArray) per map and between maps cubemaps don't interact.
		//"EnvironmentMapCacheSize"		"144"
		// For tools mode we still need to use the runtime texture array
		"EnvironmentMapCacheSizeTools"	"340"
		"GrassQuadSize"		"512"
		"GrassCompressDensity" "0"
		"GrassDilateColors"	"0"
		"GrassNoHalfTexel"	"1"
		"LPVEdgeBlending"	"0"	// Don't apply the edge fade distance to LPV bounds, we don't blend LPVs in CS2 shaders
	}
	ModelDoc
	{
		"models_gamedata"			"models_gamedata.fgd"
		"features"					"cs2;modelconfig;animgraph;animgraph_compatibility_force;editorconfig;gamepreview"
		"firstpersoncamerapreview"	"1"
		"disallowed_materials"
		{
			"material" "materials/debug/debugempty.vmat"
		}
		content_consider_missing_materials_fatal
		{
			"substr" ".vmdl"
		}
		"content_consider_warnings_as_errors"
		{
			"substr" "agents/models/"
			"substr" "weapons/models/"
		}
	}
	RenderSystem
	{
		"AllowPartialMipChainImmediateTexLoads"		"1"
		"VulkanRequireSubgroupWaveOpSupport"		"1"
		"VulkanRequireDescriptorIndexing"			"1"
		"VulkanDefrag"								"1"
		//"MaxPreloadTextureResolution"				"256"
		"IndexBufferPoolSizeMB"						"64"
		"LowLatency"								"1"
		"MinStreamingPoolSizeMB"					"500"
		"MinStreamingPoolSizeMBTools"				"2048"
		"UseHardwareGammaRamp"						"0" // Fullscreen gamma controlled in postprocessing
	}
	Manifest
	{
		"GenerateVPKManifest" "1"
	}
	Physics
	{
		"BuildMeshWings" "1"
	}
	PostProcessingEditor
	{
		"supports_local_contrast"	"1"
		"filmic_linear_scale" 		"1"
		"compute_bloom"				"0"

	}
	SoundSystem
	{
		"SteamAudioEnabled"            "1"
		Budget_StackSimulationUS		25
		Budget_FirstStackSimulationUS	50
	}
	Licenses
	{
		//OodleTexture    "0"
		//OodleLZ         "0"
	}
	Memory
	{
		"EstimatedMaxCPUMemUsageMB"	"3388"
		"EstimatedMinGPUMemUsageMB"	"1246"
	}
}
