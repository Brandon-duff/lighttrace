package main

import (
	"encoding/json"
	"fmt"
	"io/ioutil"
	"net/http"
	"os"

	"github.com/rs/cors"

	_ "github.com/jinzhu/gorm/dialects/postgres"
	log "github.com/sirupsen/logrus"
)

// Blindly pushes json Blobs to a database, keyed on the visit-id
func addEvents(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "404 not found.", http.StatusNotFound)
		return
	}
	body, err := ioutil.ReadAll(r.Body)
	if err != nil {
		http.Error(w, "error reading body contents", http.StatusConflict)
		return
	}
	bs := string(body)
	vid := r.Header.Get("X-Visit-ID")
	e := &EventsObject{Events: &bs, VisitID: &vid}
	if res := DB.Create(e); res.Error != nil {
		http.Error(w, "error reading body contents", http.StatusConflict)
		return
	}
	return
}

type EventsStruct struct {
	Events               []json.RawMessage `json:"events"`
	VisitLocationDetails json.RawMessage   `json:"visitLocationDetails"`
}

// Gets all of the 'event' blobs, puts them together, and returns it to the frontend.
// Because sessions views don't happen often (compared to writes), we want to do the processing here.
func getEvents(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, "404 not found.", http.StatusNotFound)
		return
	}
	vid := r.URL.Query().Get("vid")
	if len(vid) == 0 {
		http.Error(w, "Incomplete request.", http.StatusNotFound)
		return
	}
	eventObjs := []*EventsObject{}
	if res := DB.Order("created_at asc").Where(&EventsObject{VisitID: &vid}).Find(&eventObjs); res.Error != nil {
		http.Error(w, "error reading body contents", http.StatusConflict)
		return
	}
	allEvents := &EventsStruct{}
	for _, eventObj := range eventObjs {
		events := &EventsStruct{}
		if err := json.Unmarshal([]byte(*eventObj.Events), events); err != nil {
			http.Error(w, fmt.Sprintf("Error decoding data: %v", err), http.StatusNotFound)
			return
		}
		allEvents.VisitLocationDetails = events.VisitLocationDetails
		allEvents.Events = append(allEvents.Events, events.Events...)
	}
	j, err := json.Marshal(allEvents)
	if err != nil {
		http.Error(w, fmt.Sprintf("Error remarshaling data: %v", err), http.StatusNotFound)
		return
	}
	w.Write(j)
	return
}

func health(w http.ResponseWriter, r *http.Request) {
	w.Write([]byte("healthy"))
	return
}

var defaultPort = "8082"

func main() {
	port := os.Getenv("PORT")
	if port == "" {
		port = defaultPort
	}

	SetupDB()

	mux := http.NewServeMux()
	mux.HandleFunc("/add-events", addEvents)
	mux.HandleFunc("/get-events", getEvents)
	mux.HandleFunc("/", health)
	handler := cors.AllowAll().Handler(mux)
	fmt.Println("listening...")
	log.Fatal(http.ListenAndServe(":"+port, handler))
}
