import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, History, Play, RotateCcw, Shield, Square, Swords, Users } from "lucide-react";
import { api, type MatchCatalog, type MatchResult, type MatchSession, type PrepareMatchInput } from "../lib/api";
import { useT } from "../i18n";
import { useStore } from "../state/store";
import { listenAppEvent } from "../lib/platform";
import Toggle from "../components/Toggle";
import Dropdown from "../components/Dropdown";
import Modal from "../components/Modal";
import MatchResultView from "./MatchResultView";
import { MAP_IMAGES, MAP_LABELS } from "../data/maps";
import { teamLogoPath } from "../data/matchVisuals";
import "./MatchPanel.css";

type Props = { onOpenInstallation?: () => void; onOpenHistory?: () => void };

export default function MatchPanel({ onOpenInstallation, onOpenHistory }: Props) {
  const t = useT();
  const { directory, process, reportError } = useStore();
  const csgo = directory?.valid ? directory.selected : null;
  const [catalog, setCatalog] = useState<MatchCatalog | null>(null);
  const [result, setResult] = useState<MatchResult | null>(null);
  const [activeMatch, setActiveMatch] = useState<MatchSession | null>(null);
  const [selectedMap, setSelectedMap] = useState("de_mirage");
  const [opponentKind, setOpponentKind] = useState<PrepareMatchInput["opponent_kind"]>("featured_team");
  const [teamId, setTeamId] = useState<string | null>(null);
  const [side, setSide] = useState<PrepareMatchInput["player_side"]>("random");
  const [difficulty, setDifficulty] = useState<PrepareMatchInput["difficulty"]>(() => (localStorage.getItem("cs2bi.matchDifficulty") as PrepareMatchInput["difficulty"]) || "medium");
  const [recordDemo, setRecordDemo] = useState(localStorage.getItem("cs2bi.matchDemoV2") === "1");
  const [busy, setBusy] = useState(false);
  const [finishConfirmOpen, setFinishConfirmOpen] = useState(false);

  useEffect(() => {
    if (!csgo) return;
    void api.getMatchCatalog(csgo).then((value) => {
      setCatalog(value);
      setTeamId((current) => current && value.teams.some((team) => team.id === current) ? current : value.teams[0]?.id ?? null);
    }).catch(reportError);
    void api.getActiveMatch(csgo).then((active) => {
      if (active && (active.state === "finished" || active.state === "interrupted")) {
        void api.getMatchResult(csgo, active.session_id).then(setResult).catch(() => {});
      } else {
        setActiveMatch(active);
      }
    }).catch(() => {});
  }, [csgo]);

  useEffect(() => {
    const promise = listenAppEvent<MatchResult>("match-finished", (event) => {
      setActiveMatch(null);
      setResult(event.payload);
      setBusy(false);
    });
    return () => { void promise.then((dispose) => dispose()); };
  }, [csgo]);

  const selectedTeam = useMemo(() => catalog?.teams.find((team) => team.id === teamId) ?? null, [catalog, teamId]);
  const disabledReason = !csgo ? t("match.reasonDirectory") : process?.running ? t("match.reasonRunning") : !catalog ? t("match.reasonCatalog") : opponentKind === "featured_team" && !teamId ? t("match.reasonTeam") : null;

  const teamOptions = useMemo(() => (catalog?.teams ?? []).map((team) => ({
    value: team.id,
    label: (
      <span className="match-team-option">
        <span className="match-team-option__logo" aria-hidden="true">
          <img src={teamLogoPath(team.id)} alt="" loading="lazy" decoding="async" onError={(event) => { event.currentTarget.hidden = true; }} />
        </span>
        <span className={`match-team-option__rank ${team.ranking && team.ranking <= 5 ? "is-top" : ""}`}>
          {team.ranking ? `#${team.ranking}` : "—"}
        </span>
        <span className="match-team-option__body">
          <strong>{team.name}</strong>
          <small>{team.players.join(" · ")}</small>
        </span>
      </span>
    ),
  })), [catalog]);

  const startMatch = async () => {
    if (!csgo || disabledReason) return;
    setBusy(true);
    localStorage.setItem("cs2bi.matchDifficulty", difficulty);
    localStorage.setItem("cs2bi.matchDemoV2", recordDemo ? "1" : "0");
    try {
      const request = await api.prepareAndLaunchMatch(csgo, { schema_version: 1, map_id: selectedMap, player_side: side, difficulty, opponent_kind: opponentKind, opponent_team_id: opponentKind === "featured_team" ? teamId : null, record_demo: recordDemo });
      setActiveMatch({
        schema_version: request.schema_version,
        session_id: request.session_id,
        state: "launching",
        map_id: request.map_id,
        opponent_name: request.opponent_name,
        created_at_unix: request.created_at_unix,
        player_score: 0,
        opponent_score: 0,
        demo: { state: request.record_demo ? "pending" : "disabled", path: request.record_demo ? request.demo_path : null, size_bytes: 0, error_code: null, detail: null },
        result_path: request.result_path,
      });
      setBusy(false);
    } catch (error) { setBusy(false); reportError(error); }
  };

  const finishEarly = async () => {
    if (!csgo || !activeMatch) return;
    setFinishConfirmOpen(false);
    setBusy(true);
    try {
      await api.finishActiveMatch(csgo, activeMatch.session_id);
    } catch (error) {
      setBusy(false);
      reportError(error);
    }
  };

  if (result) return <MatchResultView result={result} onClose={() => setResult(null)} t={t} csgo={csgo} />;

  if (activeMatch) return (
    <div className="match-page match-active-page">
      <header className="workspace__head match-page__head">
        <div className="match-page__title">
          <span className="workspace__eyebrow">{t("match.activeEyebrow")}</span>
          <h1>{t("match.active")}</h1>
          <p>{t("match.activeAgainst", { team: activeMatch.opponent_name })}</p>
        </div>
        {onOpenHistory && <button className="match-history-button" onClick={onOpenHistory}><History size={16} />{t("match.history")}</button>}
      </header>
      <section className="match-active glass">
        <div className="match-active__map">
          <img src={MAP_IMAGES[activeMatch.map_id]} alt="" aria-hidden="true" />
          <span>{MAP_LABELS[activeMatch.map_id] ?? activeMatch.map_id}</span>
        </div>
        <div className="match-active__body">
          <span className="match-active__pulse" aria-hidden="true" />
          <small>{t("match.active")}</small>
          <strong>{activeMatch.opponent_name}</strong>
          <span>5v5 · MR12</span>
        </div>
        <button className="match-finish-early" disabled={busy} onClick={() => setFinishConfirmOpen(true)}>
          {busy ? <RotateCcw className="is-spinning" size={17} /> : <Square size={15} fill="currentColor" />}
          {busy ? t("match.finishingEarly") : t("match.finishEarly")}
        </button>
      </section>
      <Modal
        open={finishConfirmOpen}
        title={t("match.finishEarly")}
        onClose={() => setFinishConfirmOpen(false)}
        footer={<>
          <button className="btn-secondary" onClick={() => setFinishConfirmOpen(false)}>{t("common.cancel")}</button>
          <button className="match-confirm-accept" onClick={() => void finishEarly()}>{t("match.finishEarly")}</button>
        </>}
      >
        <p className="match-dialog-copy">{t("match.finishEarlyConfirm")}</p>
      </Modal>
    </div>
  );

  const sideLabel = side === "random" ? t("match.random") : side.toUpperCase();

  return (
    <div className="match-page">
      <header className="workspace__head match-page__head">
        <div className="match-page__title">
          <span className="workspace__eyebrow">LOCAL ARENA MATCH</span>
          <h1>{t("match.title")}</h1>
          <p>{t("match.subtitle")}</p>
        </div>
        {onOpenHistory && <button className="match-history-button" onClick={onOpenHistory}><History size={16} />{t("match.history")}</button>}
      </header>

      <section className="match-vs glass" aria-label={t("match.title")}>
        <div className="match-vs__side">
          <span className="match-vs__tag">{t("match.yourTeam")}</span>
          <span className="match-vs__avatar match-vs__avatar--you" aria-hidden="true"><Users size={22} /></span>
          <strong>{sideLabel}</strong>
          <small>{t("match.side")}</small>
        </div>
        <div className="match-vs__center">
          <span className="match-vs__mapchip" aria-hidden="true">
            <img src={MAP_IMAGES[selectedMap]} alt="" />
          </span>
          <span className="match-vs__vs">VS</span>
          <span className="match-vs__map">{MAP_LABELS[selectedMap] ?? selectedMap}</span>
          <small>5v5 · MR12</small>
        </div>
        <div className="match-vs__side match-vs__side--right">
          {opponentKind === "featured_team" && selectedTeam ? (
            <>
              <span className="match-vs__tag">{t("match.opponent")}</span>
              <span className="match-vs__avatar match-vs__avatar--team" aria-hidden="true">
                <Shield className="match-vs__avatar-fallback" size={22} />
                <img src={teamLogoPath(selectedTeam.id)} alt="" decoding="async" onError={(event) => { event.currentTarget.hidden = true; }} />
              </span>
              <strong>{selectedTeam.ranking ? `#${selectedTeam.ranking} ` : ""}{selectedTeam.name}</strong>
              <span className="match-vs__roster">
                {selectedTeam.players.map((player) => <i key={player}>{player}</i>)}
              </span>
            </>
          ) : (
            <>
              <span className="match-vs__tag">{t("match.opponent")}</span>
              <span className="match-vs__avatar match-vs__avatar--random" aria-hidden="true"><Swords size={22} /></span>
              <strong>{t("match.random")}</strong>
              <small>{t("match.randomNote")}</small>
            </>
          )}
        </div>
      </section>

      <div className="match-layout">
        <section className="match-config glass">
          <div className="match-config__section">
            <span className="match-label">{t("match.opponent")}</span>
            <div className="match-segment">
              <button className={opponentKind === "featured_team" ? "is-active" : ""} onClick={() => setOpponentKind("featured_team")}><Shield size={15} />{t("match.featured")}</button>
              <button className={opponentKind === "random" ? "is-active" : ""} onClick={() => setOpponentKind("random")}><Users size={15} />{t("match.random")}</button>
            </div>
          </div>
          {opponentKind === "featured_team" && (
            <div className="match-config__section">
              <span className="match-label">{t("match.team")}</span>
              <Dropdown
                value={teamId}
                options={teamOptions}
                placeholder={t("match.reasonTeam")}
                disabled={!catalog}
                ariaLabel={t("match.team")}
                inline
                onChange={setTeamId}
              />
            </div>
          )}
          <div className="match-config__section">
            <span className="match-label">{t("match.side")}</span>
            <div className="match-segment match-segment--three">
              {(["random", "ct", "t"] as const).map((value) => (
                <button key={value} className={side === value ? "is-active" : ""} onClick={() => setSide(value)}>
                  {value === "random" ? t("match.random") : value.toUpperCase()}
                </button>
              ))}
            </div>
          </div>
          <div className="match-config__section">
            <span className="match-label">{t("match.difficulty")}</span>
            <div className="match-segment match-segment--three">
              {(["low", "medium", "high"] as const).map((value) => (
                <button key={value} className={difficulty === value ? "is-active" : ""} onClick={() => setDifficulty(value)}>{t(`match.${value}` as never)}</button>
              ))}
            </div>
          </div>
          <div className="match-demo-row">
            <div><span className="match-label">{t("match.demo")}</span><small>{t("match.demoDesc")}</small></div>
            <Toggle checked={recordDemo} onChange={setRecordDemo} ariaLabel={t("match.demo")} />
          </div>
          {recordDemo && <div className="match-demo-warning" role="alert"><AlertTriangle size={15} /><span>{t("match.demoWarning")}</span></div>}
          {disabledReason && (
            <div className="match-disabled">
              <AlertTriangle size={15} />
              <span>{disabledReason}</span>
              {!csgo && onOpenInstallation && <button onClick={onOpenInstallation}>{t("match.openInstallation")}</button>}
            </div>
          )}
          <button className="match-start" disabled={!!disabledReason || busy} onClick={startMatch}>
            {busy ? <RotateCcw className="is-spinning" size={17} /> : <Play size={17} fill="currentColor" />}
            {busy ? t("match.launching") : t("match.start")}
          </button>
        </section>

        <section className="match-maps">
          <div className="match-section-head">
            <span className="match-label">{t("match.mapPool")}</span>
            <span className="match-map-count">{catalog?.maps.length ?? 10} {t("match.maps")}</span>
          </div>
          <div className="match-map-hero">
            <img src={MAP_IMAGES[selectedMap]} alt="" aria-hidden="true" />
            <span>{MAP_LABELS[selectedMap] ?? selectedMap}</span>
          </div>
          <div className="match-map-grid">
            {catalog?.maps.map((map) => (
              <button key={map.id} className={`match-map-card ${selectedMap === map.id ? "is-selected" : ""}`} onClick={() => setSelectedMap(map.id)}>
                <span className={`match-map-card__image map-${map.id.replace("de_", "")}`}>
                  <img src={MAP_IMAGES[map.id]} alt="" aria-hidden="true" />
                  <span>{MAP_LABELS[map.id] ?? map.display_name}</span>
                </span>
                <span className="match-map-card__name">{map.display_name}</span>
                <span className="match-map-card__check">{selectedMap === map.id ? "●" : "○"}</span>
              </button>
            ))}
          </div>
        </section>
      </div>
    </div>
  );
}
