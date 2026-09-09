"""_local_asr_dir 模型解析：不静默替换所选模型（T10 修复）。"""
from pathlib import Path

import pipeline


def test_turbo_falls_back_to_legacy_asr_dir(tmp_path, monkeypatch):
    monkeypatch.setattr(pipeline, "_MODELS_DIR", tmp_path)
    (tmp_path / "asr").mkdir()
    assert pipeline._local_asr_dir("large-v3-turbo") == str(tmp_path / "asr")


def test_turbo_prefers_own_dir_over_legacy(tmp_path, monkeypatch):
    monkeypatch.setattr(pipeline, "_MODELS_DIR", tmp_path)
    (tmp_path / "asr").mkdir()
    (tmp_path / "large-v3-turbo").mkdir()
    assert pipeline._local_asr_dir("large-v3-turbo") == str(tmp_path / "large-v3-turbo")


def test_selected_model_not_silently_substituted(tmp_path, monkeypatch):
    """选 large-v3 而本地只有 turbo 的 models/asr → 必须返回 None 走在线，而非偷用 turbo。"""
    monkeypatch.setattr(pipeline, "_MODELS_DIR", tmp_path)
    (tmp_path / "asr").mkdir()
    assert pipeline._local_asr_dir("large-v3") is None
    assert pipeline._local_asr_dir("small") is None


def test_exact_match_dir_wins(tmp_path, monkeypatch):
    monkeypatch.setattr(pipeline, "_MODELS_DIR", tmp_path)
    (tmp_path / "large-v3").mkdir()
    assert pipeline._local_asr_dir("large-v3") == str(tmp_path / "large-v3")


def test_dist_in_repo_walks_up_to_repo_models(tmp_path, monkeypatch):
    """dist-in-repo：在 dist/SubtitlesFree/engine 里跑（models 空），模型在仓库 engine/models。"""
    dist_models = tmp_path / "dist" / "SubtitlesFree" / "engine" / "models"
    dist_models.mkdir(parents=True)
    (tmp_path / "engine" / "models" / "large-v3-turbo").mkdir(parents=True)
    (tmp_path / "engine" / "models" / "align").mkdir(parents=True)
    monkeypatch.setattr(pipeline, "_MODELS_DIR", dist_models)
    assert pipeline._local_asr_dir("large-v3-turbo") == str(
        tmp_path / "engine" / "models" / "large-v3-turbo")
    assert pipeline._local_model_dir("align") == str(
        tmp_path / "engine" / "models" / "align")
    # 语义不破：选 large-v3 时仓库只有 turbo 目录 → 仍返回 None 走在线，不偷换
    assert pipeline._local_asr_dir("large-v3") is None


def test_nearest_models_root_wins(tmp_path, monkeypatch):
    dist_models = tmp_path / "dist" / "SubtitlesFree" / "engine" / "models"
    (dist_models / "large-v3-turbo").mkdir(parents=True)          # dist 自己有（如用户拷了）
    (tmp_path / "engine" / "models" / "large-v3-turbo").mkdir(parents=True)  # 仓库也有
    monkeypatch.setattr(pipeline, "_MODELS_DIR", dist_models)
    assert pipeline._local_asr_dir("large-v3-turbo") == str(dist_models / "large-v3-turbo")
