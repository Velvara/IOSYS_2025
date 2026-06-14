using UnityEngine;
using System;

public class TimeManager : MonoBehaviour
{
    // ---------------- Singleton ----------------
    public static TimeManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }


    [Header("Time Scale Settings")]
    public float secsToMins = 1f;  // real seconds per game minute
    public int minsToHrs = 60;     // game minutes per game hour
    public int hrsToDays = 24;     // game hours per game day
    public int daysToWeeks = 1;    // game days per game week
    public int weeksToMonths = 8;  // game weeks per game month
    public int monthsToYrs = 4;   // game months per game year

    [Header("Season Settings")]
    [Tooltip("How many months are in one season?")]
    public int monthsPerSeason = 1;


    [Header("Custom Names (auto-sized)")]
    public string[] dayNames;
    public string[] weekNames;
    public string[] monthNames;
    public string[] seasonNames;
    public string[] yearNames;


    [Header("Current Game Time")]
    public int gameMins;
    public int gameHrs;
    public int gameDays = 1;
    public int gameWeeks = 1;
    public int gameMonths = 1;
    public int gameYrs = 1;

    private float timer;
    private bool isPaused = false;

    [Header("Time Control")]
    public float timeMultiplier = 1f;


    // ---------------- Events ----------------
    public event Action OnMinuteChanged;
    public event Action OnHourChanged;
    public event Action OnDayChanged;
    public event Action OnWeekChanged;
    public event Action OnMonthChanged;
    public event Action OnYearChanged;
    public event Action OnSeasonChanged;

    public event Action OnPaused;
    public event Action OnResumed;


    void Start()
    {
        float realSecondsPerDay = secsToMins * minsToHrs * hrsToDays;
        float realHoursPerWeek = (realSecondsPerDay * daysToWeeks) / 3600f;
        float realHoursPerMonth = (realSecondsPerDay * daysToWeeks * weeksToMonths) / 3600f;
        float realHoursPerYear = (realSecondsPerDay * daysToWeeks * weeksToMonths * monthsToYrs) / 3600f;

        Debug.Log($"1 Game Day = {realSecondsPerDay / 60f:F2} real minutes");
        Debug.Log($"1 Game Week = {realHoursPerWeek:F2} real hours");
        Debug.Log($"1 Game Month = {realHoursPerMonth:F2} real hours");
        Debug.Log($"1 Game Year = {realHoursPerYear:F2} real hours");
    }


    void Update()
    {
        if (isPaused || timeMultiplier <= 0f) return;

        timer += Time.deltaTime * timeMultiplier;

        if (timer >= secsToMins)
        {
            timer -= secsToMins;
            AdvanceMinute();
        }
    }


    void AdvanceMinute()
    {
        gameMins++;
        OnMinuteChanged?.Invoke();

        if (gameMins >= minsToHrs)
        {
            gameMins = 0;
            gameHrs++;
            OnHourChanged?.Invoke();

            if (gameHrs >= hrsToDays)
            {
                gameHrs = 0;
                gameDays++;
                OnDayChanged?.Invoke();

                if (gameDays > daysToWeeks)
                {
                    gameDays = 1;
                    gameWeeks++;
                    OnWeekChanged?.Invoke();

                    if (gameWeeks > weeksToMonths)
                    {
                        gameWeeks = 1;
                        gameMonths++;
                        OnMonthChanged?.Invoke();

                        if ((gameMonths - 1) % monthsPerSeason == 0)
                        {
                            OnSeasonChanged?.Invoke();
                        }

                        if (gameMonths > monthsToYrs)
                        {
                            gameMonths = 1;
                            gameYrs++;
                            OnYearChanged?.Invoke();
                        }
                    }
                }
            }
        }
    }


    // ---------------- Pause / Resume ----------------
    public void PauseTime()
    {
        if (!isPaused)
        {
            isPaused = true;
            OnPaused?.Invoke();
            Debug.Log("Game time paused.");
        }
    }

    public void ResumeTime()
    {
        if (isPaused)
        {
            isPaused = false;
            OnResumed?.Invoke();
            Debug.Log("Game time resumed.");
        }
    }

    public void TogglePause()
    {
        if (isPaused) ResumeTime();
        else PauseTime();
    }

    public bool IsPaused() => isPaused;


    // ---------------- Time Multiplier ----------------
    public void SetTimeMultiplier(float multiplier)
    {
        timeMultiplier = Mathf.Max(0f, multiplier);
        Debug.Log($"Time multiplier set to {timeMultiplier}x");
    }

    public void SpeedUp(float step = 1f) => SetTimeMultiplier(timeMultiplier + step);
    public void SlowDown(float step = 1f) => SetTimeMultiplier(Mathf.Max(0f, timeMultiplier - step));
    public void ResetSpeed() => SetTimeMultiplier(1f);


    // ---------------- Naming Helpers ----------------
    string GetNameFromArray(string[] names, int index, string fallbackPrefix)
    {
        if (names != null && names.Length > 0)
        {
            return names[(index - 1) % names.Length];
        }
        return $"{fallbackPrefix} {index}";
    }

    public string GetDayName() => GetNameFromArray(dayNames, gameDays, "Day");
    public string GetWeekName() => GetNameFromArray(weekNames, gameWeeks, "Week");
    public string GetMonthName() => GetNameFromArray(monthNames, gameMonths, "Month");
    public string GetYearName() => GetNameFromArray(yearNames, gameYrs, "Year");

    public string GetSeason()
    {
        int seasonIndex = ((gameMonths - 1) / monthsPerSeason) + 1;
        return GetNameFromArray(seasonNames, seasonIndex, "Season");
    }


    // ---------------- Public Accessors ----------------
    public string GetCalendarDate()
    {
        return $"{GetDayName()} of {GetWeekName()} of {GetMonthName()} of {GetYearName()}";
    }

    public string GetClockTime(bool use24Hour = true)
    {
        if (use24Hour)
            return $"{gameHrs:00}:{gameMins:00}";
        else
        {
            int hour = gameHrs % 12;
            if (hour == 0) hour = 12;
            string ampm = gameHrs < 12 ? "AM" : "PM";
            return $"{hour:00}:{gameMins:00} {ampm}";
        }
    }

    public string GetFullDateTime(bool use24Hour = true)
    {
        return $"{GetClockTime(use24Hour)} - {GetCalendarDate()} - {GetSeason()}";
    }


    // ---------------- Inspector Sync ----------------
    void OnValidate()
    {
        // Sync array sizes to calendar definitions
        if (daysToWeeks < 1) daysToWeeks = 1;
        if (weeksToMonths < 1) weeksToMonths = 1;
        if (monthsToYrs < 1) monthsToYrs = 1;
        if (monthsPerSeason < 1) monthsPerSeason = 1;

        ResizeArray(ref dayNames, daysToWeeks, "Day");
        ResizeArray(ref weekNames, weeksToMonths, "Week");
        ResizeArray(ref monthNames, monthsToYrs, "Month");
        ResizeArray(ref seasonNames, Mathf.CeilToInt((float)monthsToYrs / monthsPerSeason), "Season");
        ResizeArray(ref yearNames, Mathf.Max(gameYrs, 1), "Year");
    }

    void ResizeArray(ref string[] array, int targetSize, string defaultPrefix)
    {
        if (array == null) array = new string[targetSize];

        if (array.Length != targetSize)
        {
            string[] newArray = new string[targetSize];
            for (int i = 0; i < targetSize; i++)
            {
                if (i < array.Length && !string.IsNullOrEmpty(array[i]))
                    newArray[i] = array[i];
                else
                    newArray[i] = $"{defaultPrefix} {i + 1}";
            }
            array = newArray;
        }
    }
}
